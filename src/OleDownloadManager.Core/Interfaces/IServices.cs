using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Models;

namespace OleDownloadManager.Core.Interfaces;

public interface ISettingsStore
{
    /// <summary>A copy of the current settings. Mutate via <see cref="UpdateAsync"/>.</summary>
    AppSettings Current { get; }

    event EventHandler<AppSettings>? SettingsChanged;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default);
}

public sealed record DestinationCheck(bool IsUsable, DownloadErrorKind? ErrorKind, string? Message, long? FreeBytes)
{
    public static DestinationCheck Ok(long? freeBytes) => new(true, null, null, freeBytes);
}

/// <summary>File-system operations for destinations, temporary data and finalization.</summary>
public interface IFileSystemService
{
    string GetDefaultDownloadDirectory();

    /// <summary>Checks that a directory exists, is writable and (when known) has room for <paramref name="requiredBytes"/>.</summary>
    DestinationCheck CheckDestination(string directory, long? requiredBytes);

    /// <summary>Free space available to the user on the volume holding <paramref name="path"/> (or its nearest existing parent). No side effects.</summary>
    long? GetAvailableSpace(string path);

    /// <summary>
    /// Applies a conflict policy. Returns the file name to use, or null when the policy is Skip and the file exists.
    /// Names reserved by other active downloads are treated as taken.
    /// </summary>
    string? ResolveConflict(string directory, string fileName, ConflictPolicy policy, ISet<string>? reservedPaths = null);

    /// <summary>Creates (or reuses) the hidden partial-data directory for a download on the destination volume.</summary>
    string EnsureTemporaryDirectory(string destinationDirectory, Guid downloadId);

    void DeleteTemporaryData(string? temporaryDirectory);

    /// <summary>Moves a fully written file into place. Never overwrites unless <paramref name="replaceExisting"/> is true.</summary>
    void FinalizeFile(string sourcePath, string destinationPath, bool replaceExisting);

    /// <summary>Tags a finished download with Mark-of-the-Web so Windows security features apply to it.</summary>
    void ApplyMarkOfTheWeb(string path, Uri source, Uri? referrer);
}

public interface ICredentialStore
{
    void Save(string key, string secret);

    string? Read(string key);

    void Delete(string key);
}

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}

public interface INotificationService
{
    void Notify(string title, string message, NotificationKind kind);
}
