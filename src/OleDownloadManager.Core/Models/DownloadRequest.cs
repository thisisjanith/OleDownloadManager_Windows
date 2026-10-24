using OleDownloadManager.Core.Enums;

namespace OleDownloadManager.Core.Models;

/// <summary>Credentials supplied for a single download. Never persisted in plaintext.</summary>
public sealed record DownloadCredentials(string UserName, string Password);

/// <summary>Everything needed to create a download. Produced by the Add dialog, browser host or media tool.</summary>
public sealed record DownloadRequest
{
    public required Uri Url { get; init; }
    public DownloadKind Kind { get; init; } = DownloadKind.Http;
    public Uri? PageUrl { get; init; }
    public string? FileName { get; init; }

    /// <summary>Null means the default folder (or the category folder when auto-organize is on).</summary>
    public string? DestinationDirectory { get; init; }
    public DownloadCategory? Category { get; init; }
    public DownloadPriority Priority { get; init; } = DownloadPriority.Normal;
    public DownloadSource Source { get; init; } = DownloadSource.User;

    /// <summary>True for "Start Now"; false for "Add to Queue".</summary>
    public bool StartNow { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public int? MaxConnections { get; init; }
    public bool AccelerationEnabled { get; init; } = true;
    public long? SpeedLimit { get; init; }
    public ConflictPolicy? ConflictPolicy { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? MediaFormat { get; init; }

    /// <summary>Custom headers. Sensitive ones are kept out of the database.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public DownloadCredentials? Credentials { get; init; }

    /// <summary>When true, secrets are saved to Windows Credential Manager so the download can resume after restart.</summary>
    public bool RememberSecrets { get; init; }

    /// <summary>Optional values from a prior probe so the engine can skip guessing.</summary>
    public long? KnownSize { get; init; }
}

/// <summary>Result of inspecting a URL before adding it (filename, size, range support).</summary>
public sealed record ProbeResult
{
    public required Uri FinalUrl { get; init; }
    public string? SuggestedFileName { get; init; }
    public long? ContentLength { get; init; }
    public bool SupportsRanges { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string? ContentType { get; init; }
    public int StatusCode { get; init; }
}
