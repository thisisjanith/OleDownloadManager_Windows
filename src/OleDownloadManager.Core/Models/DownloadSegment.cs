using OleDownloadManager.Core.Enums;

namespace OleDownloadManager.Core.Models;

/// <summary>
/// One byte range of a segmented download. Offsets are inclusive and zero-based.
/// Each segment writes to its own temporary file, so <see cref="DownloadedBytes"/>
/// equals the number of valid bytes at the start of that file.
/// </summary>
public sealed class DownloadSegment
{
    public required int Index { get; init; }
    public required long StartOffset { get; init; }

    /// <summary>Inclusive end offset. Can shrink when the segment is split for a new connection.</summary>
    public required long EndOffset { get; set; }

    public long Length => EndOffset - StartOffset + 1;
    public long DownloadedBytes { get; set; }

    /// <summary>
    /// Bytes known to be flushed to the storage device. Only this value is persisted, so after a
    /// crash or power loss the segment resumes from data that is guaranteed to be on disk.
    /// </summary>
    public long DurableBytes { get; set; }

    public long Remaining => Math.Max(0, Length - DownloadedBytes);
    public SegmentState State { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>File name inside the download's temporary directory.</summary>
    public string TempFileName => GetTempFileName(Index);

    public bool IsComplete => DownloadedBytes >= Length;

    public static string GetTempFileName(int index) => $"seg-{index:D3}.part";

    public DownloadSegment Clone() => new()
    {
        Index = Index,
        StartOffset = StartOffset,
        EndOffset = EndOffset,
        DownloadedBytes = DownloadedBytes,
        DurableBytes = DurableBytes,
        State = State,
        RetryCount = RetryCount,
        LastError = LastError,
    };
}
