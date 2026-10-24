using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Models;

namespace OleDownloadManager.Engine;

/// <summary>Reconciles persisted download state with the partial files actually on disk.</summary>
public static class ResumeManager
{
    public sealed record ReconcileResult(bool WasActive, bool PartialDataLost, bool DestinationUnavailable);

    /// <summary>
    /// Runs once per download at startup. Downloads that were running when the app stopped become
    /// Interrupted; partial data is checked so a missing file is never silently treated as resumable.
    /// </summary>
    public static ReconcileResult ReconcileOnStartup(DownloadItem item)
    {
        lock (item.SyncRoot)
        {
            var wasActive = item.Status.IsActive();
            if (wasActive)
            {
                item.Status = DownloadStatus.Interrupted;
            }

            if (item.Status == DownloadStatus.Completed)
            {
                item.FileMissing = !SafeFileExists(item.DestinationPath);
                return new ReconcileResult(false, false, false);
            }

            if (item.Status == DownloadStatus.Canceled || item.DeletedAt is not null)
            {
                return new ReconcileResult(false, false, false);
            }

            if (!IsVolumeAvailable(item.DestinationDirectory))
            {
                if (item.Status is DownloadStatus.Interrupted or DownloadStatus.Queued)
                {
                    item.Status = DownloadStatus.WaitingForDestination;
                    item.LastError = "The destination drive is not connected.";
                    item.LastErrorKind = DownloadErrorKind.DestinationUnavailable;
                }

                return new ReconcileResult(wasActive, false, true);
            }

            if (item.Status == DownloadStatus.WaitingForDestination)
            {
                // The drive is back; resume through the normal queue.
                item.Status = DownloadStatus.Interrupted;
                wasActive = true;
            }

            var lost = VerifyPartialData(item);
            if (lost)
            {
                item.LastError = item.Segments.Count == 0
                    ? "Partial data was missing, so the download will start over."
                    : "Some partial data was missing; those parts will be downloaded again.";
                item.LastErrorKind = DownloadErrorKind.PartialDataMissing;
            }

            return new ReconcileResult(wasActive, lost, false);
        }
    }

    /// <summary>
    /// Aligns recorded progress with segment files. Returns true when recorded data was missing.
    /// Longer files are fine: anything beyond the durable length is truncated when writing resumes.
    /// Caller must hold the item lock.
    /// </summary>
    public static bool VerifyPartialData(DownloadItem item)
    {
        var lost = false;
        if (!item.IsResumable || item.Segments.Count == 0)
        {
            // Non-resumable transfers always restart; keep no stale progress.
            lost = item.DownloadedBytes > 0 && item.Kind == DownloadKind.Http;
            if (item.Kind == DownloadKind.Http)
            {
                item.Segments.Clear();
                item.DownloadedBytes = 0;
            }

            return lost;
        }

        if (item.TemporaryDirectory is null)
        {
            lost = item.Segments.Any(s => s.DurableBytes > 0);
            ResetSegments(item);
            return lost;
        }

        foreach (var segment in item.Segments)
        {
            var path = Path.Combine(item.TemporaryDirectory, segment.TempFileName);
            var length = SafeFileLength(path);
            var recorded = Math.Clamp(segment.DurableBytes, 0, segment.Length);
            if (length < 0)
            {
                if (recorded > 0)
                {
                    lost = true;
                }

                recorded = 0;
            }
            else if (length < recorded)
            {
                lost = true;
                recorded = length;
            }

            segment.DownloadedBytes = recorded;
            segment.DurableBytes = recorded;
            segment.State = segment.IsComplete ? SegmentState.Completed : SegmentState.Pending;
        }

        item.DownloadedBytes = item.Segments.Sum(s => s.DownloadedBytes);
        return lost;
    }

    /// <summary>True when the drive/share that holds <paramref name="directory"/> is reachable.</summary>
    public static bool IsVolumeAvailable(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void ResetSegments(DownloadItem item)
    {
        foreach (var segment in item.Segments)
        {
            segment.DownloadedBytes = 0;
            segment.DurableBytes = 0;
            segment.State = SegmentState.Pending;
        }

        item.DownloadedBytes = 0;
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
