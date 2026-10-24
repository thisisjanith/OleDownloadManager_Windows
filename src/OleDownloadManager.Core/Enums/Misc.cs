namespace OleDownloadManager.Core.Enums;

public enum DownloadPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
}

/// <summary>What to do when the destination file already exists.</summary>
public enum ConflictPolicy
{
    /// <summary>Pick a unique name such as "file (1).zip".</summary>
    Rename = 0,
    /// <summary>Replace the existing file. Only set after explicit user confirmation.</summary>
    Replace = 1,
    /// <summary>Do not download.</summary>
    Skip = 2,
}

public enum SegmentState
{
    Pending = 0,
    Downloading = 1,
    Completed = 2,
    Failed = 3,
}

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>Why a running download was asked to stop.</summary>
public enum StopReason
{
    Pause = 0,
    Cancel = 1,
    Shutdown = 2,
}

/// <summary>Where a download request originated.</summary>
public enum DownloadSource
{
    User = 0,
    Browser = 1,
    Clipboard = 2,
    MediaTool = 3,
}

/// <summary>Which transfer mechanism handles a download.</summary>
public enum DownloadKind
{
    /// <summary>Direct HTTP/HTTPS transfer handled by the built-in engine.</summary>
    Http = 0,

    /// <summary>Media page handled by the optional external media tool (yt-dlp).</summary>
    Media = 1,
}
