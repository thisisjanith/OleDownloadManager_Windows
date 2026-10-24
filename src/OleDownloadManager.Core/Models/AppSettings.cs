using System.Text.Json;
using OleDownloadManager.Core.Enums;

namespace OleDownloadManager.Core.Models;

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>User preferences. Serialized as JSON into the Settings table; unknown or missing values fall back to defaults.</summary>
public sealed class AppSettings
{
    // ---- General ----
    /// <summary>Null means the Windows Downloads known folder.</summary>
    public string? DefaultDownloadDirectory { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool LaunchAtLogin { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool ConfirmBeforeRemove { get; set; } = true;
    public bool ClipboardMonitoring { get; set; }

    // ---- Notifications ----
    public bool NotificationsEnabled { get; set; } = true;
    public bool NotifyOnComplete { get; set; } = true;
    public bool NotifyOnFailure { get; set; } = true;
    public bool NotifyOnQueueStart { get; set; } = true;

    // ---- Connections ----
    public int MaxActiveDownloads { get; set; } = 3;
    public int DefaultConnections { get; set; } = 4;

    /// <summary>Global cap across all downloads, bytes per second. Null = unlimited.</summary>
    public long? GlobalSpeedLimit { get; set; }

    /// <summary>Default per-download cap applied to new downloads. Null = unlimited.</summary>
    public long? DefaultPerDownloadSpeedLimit { get; set; }
    public int MaxRetries { get; set; } = 5;
    public int ConnectTimeoutSeconds { get; set; } = 30;
    public int ReadTimeoutSeconds { get; set; } = 60;

    // ---- Queue & scheduling ----
    public bool QueueRunning { get; set; } = true;
    public bool AutoResumeOnStartup { get; set; } = true;

    /// <summary>When true, scheduled downloads whose time passed while the app was closed start at next launch.</summary>
    public bool RunMissedSchedules { get; set; } = true;

    /// <summary>Optional daily time at which the queue starts.</summary>
    public TimeOnly? QueueDailyStart { get; set; }

    /// <summary>Optional daily time at which the queue stops (active queue downloads are paused).</summary>
    public TimeOnly? QueueDailyStop { get; set; }

    // ---- Files ----
    public bool AutoOrganize { get; set; }
    public Dictionary<DownloadCategory, string[]> CategoryExtensions { get; set; } =
        CategoryClassifier.DefaultMappings.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());

    /// <summary>Optional explicit folder per category. Used only when <see cref="AutoOrganize"/> is on.</summary>
    public Dictionary<DownloadCategory, string> CategoryFolders { get; set; } = [];
    public bool ComputeSha256 { get; set; }
    public ConflictPolicy DefaultConflictPolicy { get; set; } = ConflictPolicy.Rename;

    // ---- Browser ----
    public bool ShowDialogForBrowserDownloads { get; set; } = true;

    // ---- Media tool (optional yt-dlp integration) ----
    public string? MediaToolPath { get; set; }
    public string MediaDefaultFormat { get; set; } = "bv*+ba/b";
    public bool MediaNoticeAcknowledged { get; set; }

    /// <summary>
    /// Opt-in: browser whose sign-in yt-dlp may use for media sites (passed as --cookies-from-browser).
    /// Null means none. OleDownloadManager itself never reads browser cookies.
    /// </summary>
    public string? MediaCookieBrowser { get; set; }

    /// <summary>Opt-in: a Netscape-format cookies.txt file passed to yt-dlp (--cookies).</summary>
    public string? MediaCookieFile { get; set; }

    // ---- UI state ----
    public WindowPlacement? MainWindowPlacement { get; set; }
    public bool DetailsPanelVisible { get; set; }
    public string SortColumn { get; set; } = "CreatedAt";
    public bool SortDescending { get; set; } = true;

    private static readonly JsonSerializerOptions CloneOptions = new();

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, CloneOptions), CloneOptions)!;

    /// <summary>Clamps values loaded from storage into supported ranges.</summary>
    public void Normalize()
    {
        MaxActiveDownloads = Math.Clamp(MaxActiveDownloads, 1, 20);
        DefaultConnections = Math.Clamp(DefaultConnections, 1, 8);
        MaxRetries = Math.Clamp(MaxRetries, 0, 50);
        ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, 5, 300);
        ReadTimeoutSeconds = Math.Clamp(ReadTimeoutSeconds, 10, 600);
        if (GlobalSpeedLimit is <= 0)
        {
            GlobalSpeedLimit = null;
        }

        if (DefaultPerDownloadSpeedLimit is <= 0)
        {
            DefaultPerDownloadSpeedLimit = null;
        }

        CategoryExtensions ??= CategoryClassifier.DefaultMappings.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        CategoryFolders ??= [];
        MediaDefaultFormat = string.IsNullOrWhiteSpace(MediaDefaultFormat) || MediaDefaultFormat == "best" ? "bv*+ba/b" : MediaDefaultFormat;
        SortColumn = string.IsNullOrWhiteSpace(SortColumn) ? "CreatedAt" : SortColumn;
    }
}
