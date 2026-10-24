namespace OleDownloadManager.Core.Interfaces;

public sealed record MediaEntry(string Id, string Title, Uri Url, TimeSpan? Duration, int Index);

/// <summary>A selectable quality. <see cref="Selector"/> is passed to the media tool; <see cref="ApproximateBytes"/> is an estimate.</summary>
public sealed record MediaQuality(string Label, string Selector, long? ApproximateBytes, bool AudioOnly);

/// <summary>Result of reading a media page. <see cref="Qualities"/> lists what this video actually offers (best first).</summary>
public sealed record MediaInspection(string Title, bool IsPlaylist, IReadOnlyList<MediaEntry> Entries, IReadOnlyList<MediaQuality> Qualities);

public sealed record MediaProgress(long DownloadedBytes, long? TotalBytes, double BytesPerSecond, string? Stage);

public sealed record MediaToolStatus(bool IsAvailable, string? Path, string? Version, string? Message, bool FfmpegAvailable = false);

/// <summary>
/// Optional integration with an external, user-installed media tool (yt-dlp).
/// OleDownloadManager does not contain site-specific extraction code itself.
/// </summary>
public interface IMediaTool
{
    Task<MediaToolStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates the tool (sites change often and old versions stop working). Returns the new status.</summary>
    Task<MediaToolStatus> UpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the entries of a playlist (or the single entry of a video page) without downloading.</summary>
    Task<MediaInspection> InspectAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads one entry into <paramref name="outputDirectory"/>. Returns the path of the produced file.
    /// </summary>
    Task<string> DownloadAsync(Uri url, string outputDirectory, string fileStem, string format,
        long? rateLimitBytesPerSecond, IProgress<MediaProgress> progress, CancellationToken cancellationToken);
}
