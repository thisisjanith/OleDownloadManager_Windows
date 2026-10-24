using OleDownloadManager.Core.Http;

namespace OleDownloadManager.Engine;

/// <summary>Tunable engine constants. Defaults suit production; tests shrink them.</summary>
public sealed class EngineOptions
{
    /// <summary>Smallest segment worth a separate connection, and the minimum split size.</summary>
    public long MinimumSegmentSize { get; set; } = SegmentCalculator.DefaultMinimumSegmentSize;

    public int BufferSize { get; set; } = 64 * 1024;

    /// <summary>How often progress snapshots are published (several times per second).</summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often segment data is flushed to disk and progress persisted.</summary>
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Retries for an individual segment before the whole attempt fails.</summary>
    public int SegmentRetries { get; set; } = 4;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ResponseHeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Abort a connection when no data arrives for this long.</summary>
    public TimeSpan ReadInactivityTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public int MaxRedirects { get; set; } = 10;

    /// <summary>Interval for schedule, queue-time and destination checks.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(5);

    public string UserAgent { get; set; } = "OleDownloadManager/1.0 (Windows)";

    /// <summary>Enables dynamic splitting of large remaining segments when a connection becomes idle.</summary>
    public bool DynamicSegmentSplitting { get; set; } = true;
}
