namespace OleDownloadManager.Core.Enums;

/// <summary>
/// Lifecycle state of a download. Values are persisted as integers; never renumber.
/// </summary>
public enum DownloadStatus
{
    /// <summary>Waiting in the queue (displayed as "Waiting").</summary>
    Queued = 0,
    Connecting = 1,
    Downloading = 2,
    Paused = 3,
    Verifying = 4,
    Completed = 5,
    Failed = 6,
    Retrying = 7,
    /// <summary>Stopped unexpectedly (application exit, crash) with progress preserved.</summary>
    Interrupted = 8,
    WaitingForDestination = 9,
    Canceled = 10,
}

public static class DownloadStatusExtensions
{
    /// <summary>True while a transfer task owns the download.</summary>
    public static bool IsActive(this DownloadStatus status) => status is
        DownloadStatus.Connecting or DownloadStatus.Downloading or
        DownloadStatus.Verifying or DownloadStatus.Retrying;

    public static bool IsTerminal(this DownloadStatus status) =>
        status is DownloadStatus.Completed or DownloadStatus.Canceled;

    public static string ToDisplayString(this DownloadStatus status) => status switch
    {
        DownloadStatus.Queued => "Waiting",
        DownloadStatus.WaitingForDestination => "Waiting for Destination",
        _ => status.ToString(),
    };
}
