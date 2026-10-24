using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using OleDownloadManager.App.Mvvm;
using OleDownloadManager.App.Services;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Infrastructure;
using OleDownloadManager.Infrastructure.Media;
using OleDownloadManager.Infrastructure.Windows;

namespace OleDownloadManager.App.ViewModels;

public sealed class CategoryRowViewModel : ObservableObject
{
    private string _extensions;
    private string _folder;

    public CategoryRowViewModel(DownloadCategory category, string extensions, string folder)
    {
        Category = category;
        _extensions = extensions;
        _folder = folder;
    }

    public DownloadCategory Category { get; }
    public string Name => Category.ToString();

    public string Extensions
    {
        get => _extensions;
        set => SetProperty(ref _extensions, value);
    }

    public string Folder
    {
        get => _folder;
        set => SetProperty(ref _folder, value);
    }
}

public sealed class BrowserStatusRow(string browser, string status, bool ok)
{
    public string Browser { get; } = browser;
    public string Status { get; } = status;
    public bool Ok { get; } = ok;
}

/// <summary>Edits a working copy of <see cref="AppSettings"/>; nothing is applied until Save.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    public static IReadOnlyList<Choice<AppTheme>> ThemeChoices { get; } =
    [
        new("Use Windows setting", AppTheme.System),
        new("Light", AppTheme.Light),
        new("Dark", AppTheme.Dark),
    ];

    public static IReadOnlyList<Choice<ConflictPolicy>> ConflictChoices { get; } =
    [
        new("Rename the new file", ConflictPolicy.Rename),
        new("Skip the download", ConflictPolicy.Skip),
    ];

    public static IReadOnlyList<Choice<long?>> SpeedChoices => AddDownloadViewModel.SpeedLimitChoices;

    public static IReadOnlyList<Choice<string>> MediaFormatChoices { get; } =
        MediaFormats.All.Select(f => new Choice<string>(f.Label, f.Selector)).ToList();

    /// <summary>Where yt-dlp may take a sign-in from for media sites. Off by default.</summary>
    public static IReadOnlyList<Choice<string?>> CookieSourceChoices { get; } =
    [
        new("Don't use a sign-in (default)", null),
        new("Firefox (most reliable)", "firefox"),
        new("Chrome", "chrome"),
        new("Edge", "edge"),
        new("Brave", "brave"),
        new("Opera", "opera"),
        new("Vivaldi", "vivaldi"),
        new("A cookies.txt file", CookieFileChoice),
    ];

    private const string CookieFileChoice = "file";

    private readonly ISettingsStore _store;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly ThemeService _theme;
    private readonly AppPaths _paths;
    private readonly IMediaTool _mediaTool;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly AppSettings _working;
    private string? _error;
    private string _mediaStatus = "";
    private string _queueStartText;
    private string _queueStopText;
    private bool _launchAtLogin;
    private Choice<string?> _cookieSource;
    private string _cookieFile;

    public SettingsViewModel(ISettingsStore store, IDialogService dialogs, IShellService shell, ThemeService theme, AppPaths paths, IMediaTool mediaTool, ILogger<SettingsViewModel> logger)
    {
        _store = store;
        _dialogs = dialogs;
        _shell = shell;
        _theme = theme;
        _paths = paths;
        _mediaTool = mediaTool;
        _logger = logger;
        _working = store.Current.Clone();
        _queueStartText = _working.QueueDailyStart?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        _queueStopText = _working.QueueDailyStop?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        _launchAtLogin = StartupRegistration.IsEnabled();
        _cookieFile = _working.MediaCookieFile ?? "";
        _cookieSource = !string.IsNullOrWhiteSpace(_working.MediaCookieFile)
            ? CookieSourceChoices[^1]
            : CookieSourceChoices.FirstOrDefault(c => c.Value == _working.MediaCookieBrowser) ?? CookieSourceChoices[0];

        foreach (var category in Enum.GetValues<DownloadCategory>().Where(c => c != DownloadCategory.Other))
        {
            var extensions = _working.CategoryExtensions.TryGetValue(category, out var list) ? string.Join(" ", list) : "";
            var folder = _working.CategoryFolders.TryGetValue(category, out var f) ? f : "";
            CategoryRows.Add(new CategoryRowViewModel(category, extensions, folder));
        }

        BrowseDefaultFolderCommand = new RelayCommand(() =>
        {
            if (_dialogs.PickFolder(DefaultDownloadDirectory) is { } folder)
            {
                DefaultDownloadDirectory = folder;
            }
        });
        BrowseCategoryFolderCommand = new RelayCommand(p =>
        {
            if (p is CategoryRowViewModel row && _dialogs.PickFolder(string.IsNullOrEmpty(row.Folder) ? DefaultDownloadDirectory : row.Folder) is { } folder)
            {
                row.Folder = folder;
            }
        });
        BrowseMediaToolCommand = new RelayCommand(BrowseMediaTool);
        BrowseCookieFileCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Cookies file|*.txt|All files|*.*", Title = "Choose a cookies.txt file" };
            if (dialog.ShowDialog() == true)
            {
                CookieFile = dialog.FileName;
            }
        });
        CheckMediaToolCommand = new AsyncRelayCommand(CheckMediaToolAsync);
        UpdateMediaToolCommand = new AsyncRelayCommand(UpdateMediaToolAsync);
        RegisterBrowserCommand = new RelayCommand(RegisterBrowser);
        UnregisterBrowserCommand = new RelayCommand(UnregisterBrowser);
        OpenLogsCommand = new RelayCommand(() => _shell.OpenFolder(_paths.LogDirectory));
        OpenDataFolderCommand = new RelayCommand(() => _shell.OpenFolder(_paths.DataDirectory));
        OpenExtensionFolderCommand = new RelayCommand(() =>
        {
            if (_shell.OpenFolder(ExtensionsDirectory) is { } error)
            {
                _dialogs.ShowError("Browser extensions", error);
            }
        });
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        RefreshBrowserStatus();
        _ = CheckMediaToolAsync();
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<CategoryRowViewModel> CategoryRows { get; } = [];
    public ObservableCollection<BrowserStatusRow> BrowserStatus { get; } = [];

    public ICommand BrowseDefaultFolderCommand { get; }
    public ICommand BrowseCategoryFolderCommand { get; }
    public ICommand BrowseMediaToolCommand { get; }
    public ICommand BrowseCookieFileCommand { get; }

    public Choice<string?> CookieSource
    {
        get => _cookieSource;
        set
        {
            if (SetProperty(ref _cookieSource, value))
            {
                OnPropertyChanged(nameof(UsesCookieFile));
            }
        }
    }

    public bool UsesCookieFile => _cookieSource.Value == CookieFileChoice;

    public string CookieFile
    {
        get => _cookieFile;
        set => SetProperty(ref _cookieFile, value);
    }
    public ICommand CheckMediaToolCommand { get; }
    public ICommand UpdateMediaToolCommand { get; }
    public ICommand RegisterBrowserCommand { get; }
    public ICommand UnregisterBrowserCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenExtensionFolderCommand { get; }
    public ICommand SaveCommand { get; }

    public string DataDirectory => _paths.DataDirectory;
    public string LogDirectory => _paths.LogDirectory;
    public string ExtensionsDirectory => Path.Combine(AppContext.BaseDirectory, "browser-extensions");

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    // ---- General ----
    public string DefaultDownloadDirectory
    {
        get => _working.DefaultDownloadDirectory ?? "";
        set
        {
            _working.DefaultDownloadDirectory = string.IsNullOrWhiteSpace(value) ? null : value;
            OnPropertyChanged();
        }
    }

    public Choice<AppTheme> Theme
    {
        get => ThemeChoices.First(c => c.Value == _working.Theme);
        set
        {
            _working.Theme = value.Value;
            _theme.Apply(value.Value);
            OnPropertyChanged();
        }
    }

    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set => SetProperty(ref _launchAtLogin, value);
    }

    public bool ShowTrayIcon
    {
        get => _working.ShowTrayIcon;
        set
        {
            _working.ShowTrayIcon = value;
            if (!value)
            {
                _working.CloseToTray = false;
                OnPropertyChanged(nameof(CloseToTray));
            }

            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => _working.CloseToTray;
        set
        {
            _working.CloseToTray = value && _working.ShowTrayIcon;
            OnPropertyChanged();
        }
    }

    public bool ConfirmBeforeRemove
    {
        get => _working.ConfirmBeforeRemove;
        set => _working.ConfirmBeforeRemove = value;
    }

    public bool ClipboardMonitoring
    {
        get => _working.ClipboardMonitoring;
        set => _working.ClipboardMonitoring = value;
    }

    public bool NotificationsEnabled
    {
        get => _working.NotificationsEnabled;
        set
        {
            _working.NotificationsEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool NotifyOnComplete
    {
        get => _working.NotifyOnComplete;
        set => _working.NotifyOnComplete = value;
    }

    public bool NotifyOnFailure
    {
        get => _working.NotifyOnFailure;
        set => _working.NotifyOnFailure = value;
    }

    public bool NotifyOnQueueStart
    {
        get => _working.NotifyOnQueueStart;
        set => _working.NotifyOnQueueStart = value;
    }

    // ---- Connections ----
    public int MaxActiveDownloads
    {
        get => _working.MaxActiveDownloads;
        set => _working.MaxActiveDownloads = Math.Clamp(value, 1, 20);
    }

    public int DefaultConnections
    {
        get => _working.DefaultConnections;
        set => _working.DefaultConnections = Math.Clamp(value, 1, 8);
    }

    public IReadOnlyList<int> ActiveChoices { get; } = Enumerable.Range(1, 10).ToList();
    public IReadOnlyList<int> ConnectionChoices => AddDownloadViewModel.ConnectionChoices;

    public Choice<long?> GlobalSpeedLimit
    {
        get => SpeedChoices.FirstOrDefault(c => c.Value == _working.GlobalSpeedLimit) ?? SpeedChoices[0];
        set => _working.GlobalSpeedLimit = value.Value;
    }

    public Choice<long?> PerDownloadSpeedLimit
    {
        get => SpeedChoices.FirstOrDefault(c => c.Value == _working.DefaultPerDownloadSpeedLimit) ?? SpeedChoices[0];
        set => _working.DefaultPerDownloadSpeedLimit = value.Value;
    }

    public int MaxRetries
    {
        get => _working.MaxRetries;
        set => _working.MaxRetries = value;
    }

    public IReadOnlyList<int> RetryChoices { get; } = [0, 1, 2, 3, 5, 10, 20];

    // ---- Queue ----
    public bool AutoResumeOnStartup
    {
        get => _working.AutoResumeOnStartup;
        set => _working.AutoResumeOnStartup = value;
    }

    public bool RunMissedSchedules
    {
        get => _working.RunMissedSchedules;
        set => _working.RunMissedSchedules = value;
    }

    public string QueueStartText
    {
        get => _queueStartText;
        set => SetProperty(ref _queueStartText, value);
    }

    public string QueueStopText
    {
        get => _queueStopText;
        set => SetProperty(ref _queueStopText, value);
    }

    // ---- Files ----
    public bool AutoOrganize
    {
        get => _working.AutoOrganize;
        set
        {
            _working.AutoOrganize = value;
            OnPropertyChanged();
        }
    }

    public bool ComputeSha256
    {
        get => _working.ComputeSha256;
        set => _working.ComputeSha256 = value;
    }

    public Choice<ConflictPolicy> DefaultConflictPolicy
    {
        get => ConflictChoices.FirstOrDefault(c => c.Value == _working.DefaultConflictPolicy) ?? ConflictChoices[0];
        set => _working.DefaultConflictPolicy = value.Value;
    }

    // ---- Browser ----
    public bool ShowDialogForBrowserDownloads
    {
        get => _working.ShowDialogForBrowserDownloads;
        set => _working.ShowDialogForBrowserDownloads = value;
    }

    // ---- Media ----
    public string MediaToolPath
    {
        get => _working.MediaToolPath ?? "";
        set
        {
            _working.MediaToolPath = string.IsNullOrWhiteSpace(value) ? null : value;
            OnPropertyChanged();
        }
    }

    public Choice<string> MediaDefaultFormat
    {
        get => MediaFormatChoices.FirstOrDefault(c => c.Value == _working.MediaDefaultFormat) ?? MediaFormatChoices[0];
        set => _working.MediaDefaultFormat = value.Value;
    }

    public string MediaStatus
    {
        get => _mediaStatus;
        private set => SetProperty(ref _mediaStatus, value);
    }

    private void BrowseMediaTool()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "yt-dlp|yt-dlp.exe|Programs|*.exe", Title = "Locate yt-dlp.exe" };
        if (dialog.ShowDialog() == true)
        {
            MediaToolPath = dialog.FileName;
        }
    }

    private async Task CheckMediaToolAsync()
    {
        MediaStatus = "Checking…";
        var status = await _mediaTool.GetStatusAsync();
        MediaStatus = status.IsAvailable ? $"Found yt-dlp {status.Version} at {status.Path}" : status.Message ?? "Not available";
    }

    private async Task UpdateMediaToolAsync()
    {
        MediaStatus = "Updating yt-dlp… this can take a minute.";
        try
        {
            var status = await _mediaTool.UpdateAsync();
            MediaStatus = $"{status.Message} Version: {status.Version ?? "unknown"}.";
        }
        catch (Exception ex)
        {
            MediaStatus = "Update failed: " + ex.Message;
        }
    }

    private void RefreshBrowserStatus()
    {
        BrowserStatus.Clear();
        foreach (var status in NativeMessagingRegistration.GetStatus())
        {
            var text = !status.Registered ? "Not registered" : status.HostExists ? "Registered" : "Registered, but the host program is missing";
            BrowserStatus.Add(new BrowserStatusRow(status.Browser, text, status.Registered && status.HostExists));
        }
    }

    private void RegisterBrowser()
    {
        try
        {
            var host = Path.Combine(AppContext.BaseDirectory, NativeMessagingRegistration.HostExecutableName);
            new NativeMessagingRegistration(_paths.NativeMessagingDirectory).Register(host,
                [NativeMessagingRegistration.DefaultChromeExtensionId], [NativeMessagingRegistration.DefaultFirefoxExtensionId]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Browser registration failed");
            _dialogs.ShowError("Browser integration", "Registration failed: " + ex.Message);
        }

        RefreshBrowserStatus();
    }

    private void UnregisterBrowser()
    {
        try
        {
            new NativeMessagingRegistration(_paths.NativeMessagingDirectory).Unregister();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Browser integration", "Could not remove the registration: " + ex.Message);
        }

        RefreshBrowserStatus();
    }

    private static bool TryParseTime(string text, out TimeOnly? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (TimeOnly.TryParseExact(text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            time = parsed;
            return true;
        }

        return false;
    }

    private async Task SaveAsync()
    {
        Error = null;
        if (!TryParseTime(_queueStartText, out var start) || !TryParseTime(_queueStopText, out var stop))
        {
            Error = "Queue times must use 24-hour HH:MM format, or be left empty.";
            return;
        }

        if (_working.DefaultDownloadDirectory is { } dir && !Path.IsPathFullyQualified(dir))
        {
            Error = "The default download folder must be a full path.";
            return;
        }

        if (UsesCookieFile && (string.IsNullOrWhiteSpace(_cookieFile) || !File.Exists(_cookieFile)))
        {
            Error = "Choose an existing cookies.txt file, or pick a different sign-in option.";
            return;
        }

        var extensions = new Dictionary<DownloadCategory, string[]>();
        var folders = new Dictionary<DownloadCategory, string>();
        foreach (var row in CategoryRows)
        {
            extensions[row.Category] = row.Extensions.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(Core.Models.CategoryClassifier.NormalizeExtension)
                .Where(e => e.Length > 1 && e.Length < 20 && e.Skip(1).All(char.IsLetterOrDigit))
                .Distinct()
                .ToArray();
            if (!string.IsNullOrWhiteSpace(row.Folder))
            {
                if (!Path.IsPathFullyQualified(row.Folder))
                {
                    Error = $"The folder for {row.Name} must be a full path.";
                    return;
                }

                folders[row.Category] = row.Folder.Trim();
            }
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (_launchAtLogin && exe is not null)
            {
                StartupRegistration.Enable(exe);
            }
            else if (!_launchAtLogin)
            {
                StartupRegistration.Disable();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning(ex, "Could not change launch-at-login");
            Error = "Could not change the launch-at-login setting: " + ex.Message;
            return;
        }

        var working = _working;
        await _store.UpdateAsync(s =>
        {
            s.DefaultDownloadDirectory = working.DefaultDownloadDirectory;
            s.Theme = working.Theme;
            s.LaunchAtLogin = _launchAtLogin;
            s.ShowTrayIcon = working.ShowTrayIcon;
            s.CloseToTray = working.CloseToTray;
            s.ConfirmBeforeRemove = working.ConfirmBeforeRemove;
            s.ClipboardMonitoring = working.ClipboardMonitoring;
            s.NotificationsEnabled = working.NotificationsEnabled;
            s.NotifyOnComplete = working.NotifyOnComplete;
            s.NotifyOnFailure = working.NotifyOnFailure;
            s.NotifyOnQueueStart = working.NotifyOnQueueStart;
            s.MaxActiveDownloads = working.MaxActiveDownloads;
            s.DefaultConnections = working.DefaultConnections;
            s.GlobalSpeedLimit = working.GlobalSpeedLimit;
            s.DefaultPerDownloadSpeedLimit = working.DefaultPerDownloadSpeedLimit;
            s.MaxRetries = working.MaxRetries;
            s.AutoResumeOnStartup = working.AutoResumeOnStartup;
            s.RunMissedSchedules = working.RunMissedSchedules;
            s.QueueDailyStart = start;
            s.QueueDailyStop = stop;
            s.AutoOrganize = working.AutoOrganize;
            s.CategoryExtensions = extensions;
            s.CategoryFolders = folders;
            s.ComputeSha256 = working.ComputeSha256;
            s.DefaultConflictPolicy = working.DefaultConflictPolicy;
            s.ShowDialogForBrowserDownloads = working.ShowDialogForBrowserDownloads;
            s.MediaToolPath = working.MediaToolPath;
            s.MediaDefaultFormat = working.MediaDefaultFormat;
            s.MediaCookieFile = UsesCookieFile ? _cookieFile.Trim() : null;
            s.MediaCookieBrowser = UsesCookieFile ? null : _cookieSource.Value;
        });

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restores the theme preview when the dialog is canceled.</summary>
    public void RevertPreview() => _theme.Apply(_store.Current.Theme);
}
