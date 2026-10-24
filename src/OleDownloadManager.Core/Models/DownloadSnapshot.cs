using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Formatting;

namespace OleDownloadManager.Core.Models;

public sealed record SegmentSnapshot(int Index, long StartOffset, long EndOffset, long DownloadedBytes, SegmentState State)
{
    public long Length => EndOffset - StartOffset + 1;
}

/// <summary>Immutable copy of a download's state, safe to hand to the UI thread.</summary>
public sealed record DownloadSnapshot
{
    public required Guid Id { get; init; }
    public DownloadKind Kind { get; init; }
    public required Uri SourceUrl { get; init; }
    public Uri? FinalUrl { get; init; }
    public Uri? PageUrl { get; init; }
    public required string FileName { get; init; }
    public required string DestinationDirectory { get; init; }
    public string DestinationPath => Path.Combine(DestinationDirectory, FileName);
    public long? ExpectedSize { get; init; }
    public long DownloadedBytes { get; init; }
    public DownloadStatus Status { get; init; }
    public DownloadPriority Priority { get; init; }
    public DownloadCategory Category { get; init; }
    public DownloadSource Source { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public int RetryCount { get; init; }
    public string? LastError { get; init; }
    public DownloadErrorKind? LastErrorKind { get; init; }
    public int? HttpStatusCode { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public bool? SupportsRangeRequests { get; init; }
    public bool AccelerationEnabled { get; init; }
    public int MaxConnections { get; init; }
    public long? SpeedLimit { get; init; }
    public long QueuePosition { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? Sha256 { get; init; }
    public double CurrentSpeed { get; init; }
    public double AverageSpeed { get; init; }
    public int ActiveConnectionCount { get; init; }
    public bool FileMissing { get; init; }
    public bool HasPartialData { get; init; }
    public string? MediaFormat { get; init; }
    public IReadOnlyList<SegmentSnapshot> Segments { get; init; } = [];

    public bool IsResumable => Kind == DownloadKind.Http && SupportsRangeRequests == true && ExpectedSize.HasValue;
    public string HostName => (FinalUrl ?? SourceUrl).Host;
    public bool IsDeleted => DeletedAt.HasValue;

    public double? ProgressFraction => ProgressMath.Fraction(DownloadedBytes, ExpectedSize, Status == DownloadStatus.Completed);
    public TimeSpan? EstimatedRemaining => Status == DownloadStatus.Downloading
        ? ProgressMath.EstimateRemaining(DownloadedBytes, ExpectedSize, CurrentSpeed)
        : null;
}
