using OleDownloadManager.Core.Models;

namespace OleDownloadManager.Core.Interfaces;

public sealed record DownloadEvent(Guid DownloadId, DateTimeOffset Timestamp, string EventType, string? Detail);

/// <summary>Persistent storage for downloads, segments and download history events.</summary>
public interface IDownloadRepository
{
    /// <summary>Opens the database and applies pending schema migrations.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DownloadItem>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces a download and its full segment map in one transaction.</summary>
    Task SaveAsync(DownloadItem item, CancellationToken cancellationToken = default);

    /// <summary>Writes progress counters (download and segment byte counts) only.</summary>
    Task SaveProgressAsync(DownloadItem item, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateQueuePositionsAsync(IReadOnlyList<(Guid Id, long Position)> positions, CancellationToken cancellationToken = default);

    Task AddEventAsync(DownloadEvent downloadEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DownloadEvent>> GetEventsAsync(Guid downloadId, CancellationToken cancellationToken = default);
}
