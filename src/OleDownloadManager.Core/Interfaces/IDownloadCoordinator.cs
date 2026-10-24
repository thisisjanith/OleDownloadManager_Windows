using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Models;

namespace OleDownloadManager.Core.Interfaces;

public sealed record AddDownloadResult(bool Accepted, Guid? DownloadId, string? Error)
{
    public static AddDownloadResult Success(Guid id) => new(true, id, null);

    public static AddDownloadResult Rejected(string error) => new(false, null, error);
}

/// <summary>
/// Central entry point for creating and controlling downloads. Owns the queue, enforces
/// concurrency limits and publishes immutable snapshots. Implementations are thread-safe;
/// events are raised on background threads.
/// </summary>
public interface IDownloadCoordinator
{
    event EventHandler<DownloadSnapshot>? DownloadUpdated;

    event EventHandler<Guid>? DownloadRemoved;

    event EventHandler? QueueStateChanged;

    bool IsQueueRunning { get; }

    int ActiveCount { get; }

    /// <summary>Loads persisted downloads, reconciles partial files and resumes eligible work.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<DownloadSnapshot> GetAll();

    DownloadSnapshot? Get(Guid id);

    /// <summary>Finds a non-deleted, unfinished download for the same URL.</summary>
    DownloadSnapshot? FindActiveDuplicate(Uri url);

    Task<ProbeResult> ProbeAsync(Uri url, IReadOnlyDictionary<string, string>? headers, DownloadCredentials? credentials, CancellationToken cancellationToken);

    Task<AddDownloadResult> AddAsync(DownloadRequest request, CancellationToken cancellationToken = default);

    Task StartAsync(IEnumerable<Guid> ids);

    /// <summary>Pauses downloads; completes after partial state has been persisted.</summary>
    Task PauseAsync(IEnumerable<Guid> ids);

    Task CancelAsync(IEnumerable<Guid> ids);

    Task RetryAsync(IEnumerable<Guid> ids);

    /// <summary>Restarts a completed or canceled download from scratch (explicit user action).</summary>
    Task RedownloadAsync(IEnumerable<Guid> ids);

    /// <summary>Moves downloads to Trash (stopping them first). Files on disk are left untouched.</summary>
    Task MoveToTrashAsync(IEnumerable<Guid> ids);

    Task RestoreFromTrashAsync(IEnumerable<Guid> ids);

    /// <summary>Permanently removes records. Partial data is deleted; completed files are kept.</summary>
    Task DeletePermanentlyAsync(IEnumerable<Guid> ids);

    /// <summary>Deletes partial data of a stopped download; it will restart from zero.</summary>
    Task DeleteIncompleteDataAsync(IEnumerable<Guid> ids);

    Task SetPriorityAsync(IEnumerable<Guid> ids, DownloadPriority priority);

    /// <summary>Moves a queued download up (negative) or down (positive) in the queue order.</summary>
    Task MoveInQueueAsync(Guid id, int delta);

    Task SetScheduleAsync(IEnumerable<Guid> ids, DateTimeOffset? scheduledAt);

    Task SetSpeedLimitAsync(IEnumerable<Guid> ids, long? bytesPerSecond);

    Task StartQueueAsync();

    /// <summary>Stops starting new queued downloads and pauses the running ones.</summary>
    Task StopQueueAsync();

    Task PauseAllAsync();

    Task ResumeAllAsync();

    /// <summary>Stops all transfers, persisting resumable state, before the app exits.</summary>
    Task ShutdownAsync();
}
