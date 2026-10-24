using System.Globalization;
using OleDownloadManager.App.Mvvm;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Formatting;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.StateMachine;

namespace OleDownloadManager.App.ViewModels;

/// <summary>Visual grouping of statuses used for colouring.</summary>
public enum StatusTone
{
    Neutral,
    Active,
    Success,
    Warning,
    Error,
}

/// <summary>One row of the download list. Updated from immutable engine snapshots on the UI thread.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private DownloadSnapshot _snapshot;

    public DownloadItemViewModel(DownloadSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public DownloadSnapshot Snapshot => _snapshot;
    public Guid Id => _snapshot.Id;

    // ---- Values used for display, sorting and filtering ----
    public string FileName => _snapshot.FileName;
    public string HostName => _snapshot.HostName;
    public string SourceUrl => _snapshot.SourceUrl.AbsoluteUri;
    public string? FinalUrl => _snapshot.FinalUrl is { } final && final != _snapshot.SourceUrl ? final.AbsoluteUri : null;
    public string? PageUrl => _snapshot.PageUrl?.AbsoluteUri;
    public string DestinationPath => _snapshot.DestinationPath;
    public string DestinationDirectory => _snapshot.DestinationDirectory;
    public DownloadStatus Status => _snapshot.Status;
    public DownloadCategory Category => _snapshot.Category;
    public bool IsDeleted => _snapshot.IsDeleted;
    public bool IsScheduled => _snapshot.ScheduledAt is not null && _snapshot.Status == DownloadStatus.Queued;
    public DateTimeOffset CreatedAt => _snapshot.CreatedAt;
    public long SortSize => _snapshot.ExpectedSize ?? _snapshot.DownloadedBytes;
    public double SortProgress => _snapshot.ProgressFraction ?? 0;
    public bool IsMedia => _snapshot.Kind == DownloadKind.Media;

    public string StatusText => _snapshot.Status switch
    {
        DownloadStatus.Queued when _snapshot.ScheduledAt is { } at => "Scheduled " + at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        DownloadStatus.Completed when _snapshot.FileMissing => "Completed (file missing)",
        DownloadStatus.Downloading when _snapshot.ActiveConnectionCount > 1 => $"Downloading · {_snapshot.ActiveConnectionCount} connections",
        _ => _snapshot.Status.ToDisplayString(),
    };

    public StatusTone Tone => _snapshot.Status switch
    {
        DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Verifying => StatusTone.Active,
        DownloadStatus.Completed when _snapshot.FileMissing => StatusTone.Warning,
        DownloadStatus.Completed => StatusTone.Success,
        DownloadStatus.Failed => StatusTone.Error,
        DownloadStatus.Retrying or DownloadStatus.Interrupted or DownloadStatus.WaitingForDestination => StatusTone.Warning,
        _ => StatusTone.Neutral,
    };

    public double ProgressPercent => (_snapshot.ProgressFraction ?? 0) * 100;
    public bool IsProgressIndeterminate => _snapshot.ProgressFraction is null && _snapshot.Status == DownloadStatus.Downloading;
    public string ProgressText => _snapshot.ProgressFraction is { } f ? (f * 100).ToString("0", CultureInfo.CurrentCulture) + "%" : "";

    public string SizeText
    {
        get
        {
            var total = _snapshot.ExpectedSize;
            if (_snapshot.Status == DownloadStatus.Completed)
            {
                return ByteFormatter.FormatSize(total ?? _snapshot.DownloadedBytes);
            }

            if (total is null)
            {
                return _snapshot.DownloadedBytes > 0 ? ByteFormatter.FormatSize(_snapshot.DownloadedBytes) + " of unknown" : "Unknown size";
            }

            return _snapshot.DownloadedBytes > 0
                ? $"{ByteFormatter.FormatSize(_snapshot.DownloadedBytes)} of {ByteFormatter.FormatSize(total.Value)}"
                : ByteFormatter.FormatSize(total.Value);
        }
    }

    public string SpeedText => _snapshot.Status == DownloadStatus.Downloading ? ByteFormatter.FormatSpeed(_snapshot.CurrentSpeed) : "";
    public string EtaText => ByteFormatter.FormatDuration(_snapshot.EstimatedRemaining);

    /// <summary>Speed and time left for the progress cell, e.g. "2.1 MB/s · 3m 05s left".</summary>
    public string TransferText => string.Join(" · ", new[] { SpeedText, EtaText.Length > 0 ? EtaText + " left" : "" }.Where(t => t.Length > 0));
    public string CreatedText => _snapshot.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string? LastError => _snapshot.LastError;
    public bool HasMessage => !string.IsNullOrEmpty(_snapshot.LastError);

    public string CategoryGlyph => _snapshot.Category switch
    {
        DownloadCategory.Applications => Glyphs.Apps,
        DownloadCategory.Documents => Glyphs.Document,
        DownloadCategory.Archives => Glyphs.Archive,
        DownloadCategory.Video => Glyphs.Video,
        DownloadCategory.Audio => Glyphs.Audio,
        DownloadCategory.Images => Glyphs.Image,
        _ => Glyphs.Page,
    };

    // ---- Capabilities ----
    public bool CanStart => !IsDeleted && (DownloadStateMachine.CanStart(Status) || (Status == DownloadStatus.Queued && !Status.IsActive()));
    public bool CanPause => !IsDeleted && DownloadStateMachine.CanPause(Status);
    public bool CanRetry => !IsDeleted && DownloadStateMachine.CanRetry(Status);
    public bool CanCancel => !IsDeleted && DownloadStateMachine.CanCancel(Status);
    public bool CanRedownload => Status is DownloadStatus.Completed or DownloadStatus.Canceled;
    public bool CanOpen => Status == DownloadStatus.Completed && !_snapshot.FileMissing;
    public bool CanReveal => Status == DownloadStatus.Completed ? !_snapshot.FileMissing : System.IO.Directory.Exists(DestinationDirectory);
    public bool CanDeleteIncompleteData => _snapshot.HasPartialData && !Status.IsActive() && Status != DownloadStatus.Completed;

    /// <summary>Glyph and tooltip for the main row button (pause / resume / retry / open).</summary>
    public string PrimaryActionGlyph => Status switch
    {
        _ when CanOpen => Glyphs.OpenFile,
        DownloadStatus.Failed => Glyphs.Retry,
        _ when Status.IsActive() || Status == DownloadStatus.Queued => Glyphs.Pause,
        _ when CanStart => Glyphs.Play,
        _ => Glyphs.OpenFile,
    };

    public string PrimaryActionText => Status switch
    {
        _ when CanOpen => "Open file",
        DownloadStatus.Failed => "Retry",
        _ when Status.IsActive() || Status == DownloadStatus.Queued => "Pause",
        _ when CanStart => "Resume",
        _ => "Open file",
    };

    // ---- Details panel ----
    public string DetailSize => _snapshot.ExpectedSize is { } size ? $"{ByteFormatter.FormatSize(size)} ({size.ToString("N0", CultureInfo.CurrentCulture)} bytes)" : "Unknown";
    public string DetailDownloaded => $"{ByteFormatter.FormatSize(_snapshot.DownloadedBytes)} ({_snapshot.DownloadedBytes.ToString("N0", CultureInfo.CurrentCulture)} bytes)";
    public string DetailAverageSpeed => ByteFormatter.FormatSpeed(_snapshot.AverageSpeed);
    public string DetailResume => _snapshot.Kind == DownloadKind.Media
        ? "Handled by the media tool"
        : _snapshot.SupportsRangeRequests switch
        {
            true => "Supported (byte ranges)",
            false => "Not supported — pausing restarts the download",
            null => "Not checked yet",
        };

    public string DetailConnections => _snapshot.AccelerationEnabled ? $"Up to {_snapshot.MaxConnections}" : "1 (acceleration off)";
    public string DetailSpeedLimit => _snapshot.SpeedLimit is { } limit ? ByteFormatter.FormatSpeed(limit) : "Unlimited";
    public string? DetailETag => _snapshot.ETag;
    public string? DetailLastModified => _snapshot.LastModified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string? DetailCompleted => _snapshot.CompletedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string? DetailSha256 => _snapshot.Sha256;
    public string? DetailExpectedSha256 => _snapshot.ExpectedSha256;
    public string DetailRetries => _snapshot.RetryCount.ToString(CultureInfo.CurrentCulture);
    public string DetailPriority => _snapshot.Priority.ToString();
    public string? DetailHttpStatus => _snapshot.HttpStatusCode?.ToString(CultureInfo.InvariantCulture);
    public IReadOnlyList<SegmentSnapshot> Segments => _snapshot.Segments;
    public long TotalForSegments => _snapshot.ExpectedSize ?? 0;

    private static readonly string[] AllProperties = typeof(DownloadItemViewModel).GetProperties()
        .Where(p => p.Name is not (nameof(Snapshot) or nameof(Id)))
        .Select(p => p.Name)
        .ToArray();

    /// <summary>Applies a newer snapshot. Returns true when list membership (status, trash, category, schedule) changed.</summary>
    public bool Update(DownloadSnapshot snapshot)
    {
        var previous = _snapshot;
        _snapshot = snapshot;
        var membershipChanged = previous.Status != snapshot.Status || previous.IsDeleted != snapshot.IsDeleted ||
                                previous.Category != snapshot.Category || previous.ScheduledAt != snapshot.ScheduledAt;

        if (!membershipChanged && previous.FileName == snapshot.FileName && previous.LastError == snapshot.LastError &&
            previous.ExpectedSize == snapshot.ExpectedSize && previous.FileMissing == snapshot.FileMissing && previous.Priority == snapshot.Priority)
        {
            // Progress-only tick: refresh the fast-changing values.
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(EtaText));
            OnPropertyChanged(nameof(TransferText));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(SortProgress));
            OnPropertyChanged(nameof(DetailDownloaded));
            OnPropertyChanged(nameof(DetailAverageSpeed));
            OnPropertyChanged(nameof(Segments));
            return false;
        }

        foreach (var name in AllProperties)
        {
            OnPropertyChanged(name);
        }

        return membershipChanged;
    }
}

public enum NavKind
{
    All,
    Downloading,
    Queue,
    Paused,
    Completed,
    Failed,
    Scheduled,
    Trash,
    Category,
}

/// <summary>Sidebar entry with a live count.</summary>
public sealed class NavItemViewModel : ObservableObject
{
    private int _count;
    private bool _isSelected;

    public NavItemViewModel(NavKind kind, string title, string glyph, DownloadCategory? category = null)
    {
        Kind = kind;
        Title = title;
        Glyph = glyph;
        Category = category;
    }

    public NavKind Kind { get; }
    public string Title { get; }
    public string Glyph { get; }
    public DownloadCategory? Category { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    public string CountText => _count > 0 ? _count.ToString(CultureInfo.CurrentCulture) : "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value)
            {
                Selected?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? Selected;

    /// <summary>Whether a download belongs to this sidebar entry.</summary>
    public bool Matches(DownloadItemViewModel item)
    {
        if (Kind == NavKind.Trash)
        {
            return item.IsDeleted;
        }

        if (item.IsDeleted)
        {
            return false;
        }

        return Kind switch
        {
            NavKind.All => true,
            NavKind.Downloading => item.Status.IsActive(),
            NavKind.Queue => item.Status == DownloadStatus.Queued && !item.IsScheduled,
            NavKind.Paused => item.Status is DownloadStatus.Paused or DownloadStatus.Interrupted or DownloadStatus.WaitingForDestination,
            NavKind.Completed => item.Status == DownloadStatus.Completed,
            NavKind.Failed => item.Status is DownloadStatus.Failed or DownloadStatus.Canceled,
            NavKind.Scheduled => item.IsScheduled,
            NavKind.Category => item.Category == Category,
            _ => false,
        };
    }
}
