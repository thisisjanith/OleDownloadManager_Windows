using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Formatting;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.Infrastructure.FileSystem;

/// <summary>Disk-space arithmetic, kept pure for testing.</summary>
public static class DiskSpace
{
    /// <summary>Space left untouched so a download never fills a drive completely.</summary>
    public const long SafetyMargin = 16L * 1024 * 1024;

    public static bool HasEnoughSpace(long requiredBytes, long availableBytes) =>
        requiredBytes <= 0 || (availableBytes >= SafetyMargin && requiredBytes <= availableBytes - SafetyMargin);
}

/// <summary>Windows file-system operations for destinations, temporary data and finalization.</summary>
public sealed partial class FileSystemService : IFileSystemService
{
    public const string PartialDirectoryName = ".oledm-partial";

    /// <summary>Classic MAX_PATH; names are shortened to stay below it when possible for compatibility with other apps.</summary>
    private const int PreferredMaxPath = 259;

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    private readonly ILogger<FileSystemService> _logger;

    public FileSystemService(ILogger<FileSystemService> logger)
    {
        _logger = logger;
    }

    public string GetDefaultDownloadDirectory()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var pathPtr) == 0)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pathPtr);
                    if (!string.IsNullOrEmpty(path))
                    {
                        return path;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "Known folder lookup failed");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public DestinationCheck CheckDestination(string directory, long? requiredBytes)
    {
        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new DestinationCheck(false, DownloadErrorKind.AccessDenied, "The destination folder path is not valid.", null);
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return new DestinationCheck(false, DownloadErrorKind.DestinationUnavailable,
                $"The destination drive ({root}) is not available. Reconnect it to continue.", null);
        }

        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, $".oledm-write-test-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new DestinationCheck(false, DownloadErrorKind.AccessDenied, "You do not have permission to write to the destination folder.", null);
        }
        catch (IOException ex)
        {
            var classified = OleDownloadManager.Engine.DownloadErrorClassifier.ClassifyFileSystem(ex);
            return new DestinationCheck(false, classified.Kind, classified.Message, null);
        }

        long? free = GetDiskFreeSpaceEx(full, out var available, out _, out _) ? (long)available : null;
        if (requiredBytes is { } required && free is { } freeBytes && !DiskSpace.HasEnoughSpace(required, freeBytes))
        {
            return new DestinationCheck(false, DownloadErrorKind.DiskFull,
                $"Not enough free space: {ByteFormatter.FormatSize(required)} is needed but only {ByteFormatter.FormatSize(freeBytes)} is available.", free);
        }

        return DestinationCheck.Ok(free);
    }

    public long? GetAvailableSpace(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
            }

            return !string.IsNullOrEmpty(current) && GetDiskFreeSpaceEx(current, out var available, out _, out _) ? (long)available : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    public string? ResolveConflict(string directory, string fileName, ConflictPolicy policy, ISet<string>? reservedPaths = null)
    {
        var name = FitToPath(directory, FileNameSanitizer.Sanitize(fileName));
        var path = SafeCombine(directory, name);
        var existsOnDisk = File.Exists(path) || Directory.Exists(path);
        var reserved = reservedPaths?.Contains(path) == true;
        if (!existsOnDisk && !reserved)
        {
            return name;
        }

        if (existsOnDisk && !reserved)
        {
            if (policy == ConflictPolicy.Skip)
            {
                return null;
            }

            if (policy == ConflictPolicy.Replace && !Directory.Exists(path))
            {
                return name;
            }
        }

        for (var counter = 1; counter < 10_000; counter++)
        {
            var candidate = FitToPath(directory, FileNameSanitizer.WithCounter(name, counter));
            var candidatePath = SafeCombine(directory, candidate);
            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath) && reservedPaths?.Contains(candidatePath) != true)
            {
                return candidate;
            }
        }

        return FileNameSanitizer.WithCounter(name, Environment.TickCount & 0x7FFFFFFF);
    }

    public string EnsureTemporaryDirectory(string destinationDirectory, Guid downloadId)
    {
        var parent = Path.Combine(Path.GetFullPath(destinationDirectory), PartialDirectoryName);
        var info = Directory.CreateDirectory(parent);
        try
        {
            if (!info.Attributes.HasFlag(FileAttributes.Hidden))
            {
                info.Attributes |= FileAttributes.Hidden;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not hide the partial-data folder");
        }

        return Directory.CreateDirectory(Path.Combine(parent, downloadId.ToString("N"))).FullName;
    }

    public void DeleteTemporaryData(string? temporaryDirectory)
    {
        if (string.IsNullOrEmpty(temporaryDirectory))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(temporaryDirectory);
            var parent = Path.GetDirectoryName(full);
            // Safety: only ever delete our own partial-data folders.
            if (parent is null || !string.Equals(Path.GetFileName(parent), PartialDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Refusing to delete unexpected temporary path {Path}", full);
                return;
            }

            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }

            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete temporary data at {Path}", temporaryDirectory);
        }
    }

    public void FinalizeFile(string sourcePath, string destinationPath, bool replaceExisting)
    {
        // Same-volume rename: atomic, and it never overwrites unless explicitly requested.
        File.Move(sourcePath, destinationPath, overwrite: replaceExisting);
    }

    public void ApplyMarkOfTheWeb(string path, Uri source, Uri? referrer)
    {
        var builder = new StringBuilder();
        builder.Append("[ZoneTransfer]\r\nZoneId=3\r\n");
        if (referrer is not null)
        {
            builder.Append("ReferrerUrl=").Append(referrer.GetLeftPart(UriPartial.Path)).Append("\r\n");
        }

        builder.Append("HostUrl=").Append(source.GetLeftPart(UriPartial.Path)).Append("\r\n");
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", builder.ToString(), Encoding.ASCII);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // FAT32/exFAT drives cannot store alternate data streams.
            _logger.LogDebug(ex, "Could not write Mark-of-the-Web for {File}", Path.GetFileName(path));
        }
    }

    /// <summary>Combines and verifies the result stays inside <paramref name="directory"/>.</summary>
    internal static string SafeCombine(string directory, string fileName)
    {
        var root = Path.GetFullPath(directory);
        var combined = Path.GetFullPath(Path.Combine(root, fileName));
        var parent = Path.GetDirectoryName(combined);
        if (!string.Equals(parent?.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The file name would place the file outside the destination folder.");
        }

        return combined;
    }

    /// <summary>Shortens the name so the full path stays under MAX_PATH when the folder allows it.</summary>
    internal static string FitToPath(string directory, string fileName)
    {
        var dirLength = Path.GetFullPath(directory).TrimEnd('\\').Length + 1;
        var available = PreferredMaxPath - dirLength;
        if (fileName.Length <= available || available < 24)
        {
            return fileName;
        }

        return FileNameSanitizer.Truncate(fileName, available);
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
}
