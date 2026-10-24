using OleDownloadManager.Core.Enums;

namespace OleDownloadManager.Core.Models;

/// <summary>
/// Mutable download record owned by the download coordinator. Other components
/// receive immutable <see cref="DownloadSnapshot"/> copies. Mutations must happen
/// while holding <see cref="SyncRoot"/>.
/// </summary>
public sealed class DownloadItem
{
    public object SyncRoot { get; } = new();

    public required Guid Id { get; init; }
    public DownloadKind Kind { get; set; } = DownloadKind.Http;
    public required Uri SourceUrl { get; set; }
    public Uri? FinalUrl { get; set; }
    public Uri? PageUrl { get; set; }
    public required string FileName { get; set; }

    /// <summary>True while the name was derived from the URL and may be replaced by the server's Content-Disposition name.</summary>
    public bool AutoFileName { get; set; }

    public required string DestinationDirectory { get; set; }
    public string DestinationPath => Path.Combine(DestinationDirectory, FileName);

    /// <summary>Directory that holds partial data. Placed on the destination volume.</summary>
    public string? TemporaryDirectory { get; set; }

    /// <summary>Total size in bytes, or null when the server did not report it.</summary>
    public long? ExpectedSize { get; set; }
    public long DownloadedBytes { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public DownloadPriority Priority { get; set; } = DownloadPriority.Normal;
    public DownloadCategory Category { get; set; }
    public DownloadSource Source { get; set; }
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Rename;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }

    /// <summary>When set, the record is in the Trash.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DownloadErrorKind? LastErrorKind { get; set; }
    public int? HttpStatusCode { get; set; }

    // Source validators used to detect a changed representation on resume.
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>Null until the server has been probed.</summary>
    public bool? SupportsRangeRequests { get; set; }

    public bool AccelerationEnabled { get; set; } = true;
    public int MaxConnections { get; set; } = 4;

    /// <summary>Per-download limit in bytes per second; null means unlimited.</summary>
    public long? SpeedLimit { get; set; }

    public long QueuePosition { get; set; }

    /// <summary>User-supplied reference checksum (lowercase hex).</summary>
    public string? ExpectedSha256 { get; set; }

    /// <summary>Locally computed checksum of the final file.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Non-sensitive custom request headers. Secrets are never stored here.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Credential Manager target name holding secrets for this download, if any.</summary>
    public string? CredentialKey { get; set; }

    /// <summary>Format selector passed to the media tool (media downloads only).</summary>
    public string? MediaFormat { get; set; }

    public List<DownloadSegment> Segments { get; set; } = [];

    // ---- Runtime-only values (not persisted) ----
    public double CurrentSpeed { get; set; }
    public double AverageSpeed { get; set; }
    public int ActiveConnectionCount { get; set; }
    public bool FileMissing { get; set; }

    /// <summary>Resuming requires a known size and confirmed byte-range support.</summary>
    public bool IsResumable => Kind == DownloadKind.Http && SupportsRangeRequests == true && ExpectedSize.HasValue;

    public DownloadSnapshot ToSnapshot()
    {
        lock (SyncRoot)
        {
            return new DownloadSnapshot
            {
                Id = Id,
                Kind = Kind,
                SourceUrl = SourceUrl,
                FinalUrl = FinalUrl,
                PageUrl = PageUrl,
                FileName = FileName,
                DestinationDirectory = DestinationDirectory,
                ExpectedSize = ExpectedSize,
                DownloadedBytes = DownloadedBytes,
                Status = Status,
                Priority = Priority,
                Category = Category,
                Source = Source,
                CreatedAt = CreatedAt,
                StartedAt = StartedAt,
                CompletedAt = CompletedAt,
                ScheduledAt = ScheduledAt,
                DeletedAt = DeletedAt,
                RetryCount = RetryCount,
                LastError = LastError,
                LastErrorKind = LastErrorKind,
                HttpStatusCode = HttpStatusCode,
                ETag = ETag,
                LastModified = LastModified,
                SupportsRangeRequests = SupportsRangeRequests,
                AccelerationEnabled = AccelerationEnabled,
                MaxConnections = MaxConnections,
                SpeedLimit = SpeedLimit,
                QueuePosition = QueuePosition,
                ExpectedSha256 = ExpectedSha256,
                Sha256 = Sha256,
                CurrentSpeed = CurrentSpeed,
                AverageSpeed = AverageSpeed,
                ActiveConnectionCount = ActiveConnectionCount,
                FileMissing = FileMissing,
                HasPartialData = DownloadedBytes > 0 || Segments.Any(s => s.DownloadedBytes > 0),
                MediaFormat = MediaFormat,
                Segments = Segments.Select(s => new SegmentSnapshot(s.Index, s.StartOffset, s.EndOffset, s.DownloadedBytes, s.State)).ToArray(),
            };
        }
    }
}
