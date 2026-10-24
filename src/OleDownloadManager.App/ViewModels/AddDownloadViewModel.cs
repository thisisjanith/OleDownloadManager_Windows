using System.Globalization;
using System.IO;
using System.Windows.Input;
using OleDownloadManager.App.Mvvm;
using OleDownloadManager.App.Services;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Formatting;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.App.ViewModels;

public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// Add Download dialog. Probes the URL (debounced) to suggest a file name and show size and resume support,
/// validates every field, and hands a <see cref="DownloadRequest"/> to the coordinator.
/// </summary>
public sealed class AddDownloadViewModel : ObservableObject
{
    public static IReadOnlyList<Choice<long?>> SpeedLimitChoices { get; } =
    [
        new("Unlimited", null),
        new("256 KB/s", 256 * 1024),
        new("512 KB/s", 512 * 1024),
        new("1 MB/s", 1024 * 1024),
        new("2 MB/s", 2 * 1024 * 1024),
        new("5 MB/s", 5 * 1024 * 1024),
        new("10 MB/s", 10 * 1024 * 1024),
    ];

    public static IReadOnlyList<Choice<DownloadCategory>> CategoryChoices { get; } =
        Enum.GetValues<DownloadCategory>().Select(c => new Choice<DownloadCategory>(c.ToString(), c)).ToList();

    public static IReadOnlyList<int> ConnectionChoices { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    public static IReadOnlyList<Choice<DownloadPriority>> PriorityChoices { get; } =
    [
        new("High", DownloadPriority.High),
        new("Normal", DownloadPriority.Normal),
        new("Low", DownloadPriority.Low),
    ];

    private readonly IDownloadCoordinator _coordinator;
    private readonly ISettingsStore _settings;
    private readonly IFileSystemService _fileSystem;
    private readonly IDialogService _dialogs;
    private readonly DownloadSource _source;
    private readonly Uri? _pageUrl;
    private CancellationTokenSource? _probeCts;
    private string _url = "";
    private string? _urlError;
    private string _fileName = "";
    private bool _fileNameEdited;
    private string _destination;
    private bool _destinationEdited;
    private Choice<DownloadCategory> _category;
    private bool _categoryEdited;
    private int _connections;
    private bool _acceleration = true;
    private Choice<long?> _speedLimit;
    private Choice<DownloadPriority> _priority;
    private bool _scheduleEnabled;
    private string _scheduleText;
    private string _headersText = "";
    private string _userName = "";
    private bool _rememberCredentials;
    private string _expectedSha256 = "";
    private ConflictPolicy _conflictPolicy;
    private bool _showAdvanced;
    private bool _isProbing;
    private string _probeSummary = "";
    private string? _errorMessage;
    private string? _duplicateNotice;
    private ProbeResult? _probe;
    private bool _isMediaPage;
    private bool _isWebPage;

    public AddDownloadViewModel(IDownloadCoordinator coordinator, ISettingsStore settings, IFileSystemService fileSystem, IDialogService dialogs, AddDownloadPrefill? prefill)
    {
        _coordinator = coordinator;
        _settings = settings;
        _fileSystem = fileSystem;
        _dialogs = dialogs;
        _source = prefill?.Source ?? DownloadSource.User;
        _pageUrl = Uri.TryCreate(prefill?.PageUrl, UriKind.Absolute, out var page) ? page : null;

        var current = settings.Current;
        _destination = string.IsNullOrWhiteSpace(current.DefaultDownloadDirectory) ? fileSystem.GetDefaultDownloadDirectory() : current.DefaultDownloadDirectory;
        _connections = current.DefaultConnections;
        _speedLimit = SpeedLimitChoices.FirstOrDefault(c => c.Value == current.DefaultPerDownloadSpeedLimit) ?? SpeedLimitChoices[0];
        _priority = PriorityChoices[1];
        _category = CategoryChoices.First(c => c.Value == DownloadCategory.Other);
        _conflictPolicy = current.DefaultConflictPolicy == ConflictPolicy.Replace ? ConflictPolicy.Rename : current.DefaultConflictPolicy;
        _scheduleText = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        BrowseCommand = new RelayCommand(Browse);
        StartNowCommand = new AsyncRelayCommand(() => SubmitAsync(startNow: true), () => CanSubmit);
        AddToQueueCommand = new AsyncRelayCommand(() => SubmitAsync(startNow: false), () => CanSubmit);
        OpenAsMediaCommand = new RelayCommand(OpenAsMedia, () => UrlError is null && !string.IsNullOrWhiteSpace(_url));

        if (!string.IsNullOrWhiteSpace(prefill?.FileName))
        {
            _fileName = FileNameSanitizer.Sanitize(prefill.FileName);
            _fileNameEdited = true;
            UpdateCategoryFromName();
        }

        if (!string.IsNullOrWhiteSpace(prefill?.Url))
        {
            Url = prefill.Url;
        }
    }

    /// <summary>Raised with true when the dialog should close after a successful add.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Supplies the password from the PasswordBox (not bindable by design).</summary>
    public Func<string>? PasswordProvider { get; set; }

    public ICommand BrowseCommand { get; }
    public ICommand StartNowCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand OpenAsMediaCommand { get; }

    /// <summary>Set when the user should continue in the media dialog (the address is a video page).</summary>
    public string? MediaRequestedUrl { get; private set; }

    /// <summary>The address is a known video page (YouTube etc.), so the media tool is needed.</summary>
    public bool IsMediaPage
    {
        get => _isMediaPage;
        private set => SetProperty(ref _isMediaPage, value);
    }

    /// <summary>The server answered with an HTML page rather than a file.</summary>
    public bool IsWebPage
    {
        get => _isWebPage;
        private set => SetProperty(ref _isWebPage, value);
    }

    public string Url
    {
        get => _url;
        set
        {
            if (SetProperty(ref _url, value))
            {
                ValidateUrl();
                ScheduleProbe();
            }
        }
    }

    public string? UrlError
    {
        get => _urlError;
        private set => SetProperty(ref _urlError, value);
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (SetProperty(ref _fileName, value))
            {
                _fileNameEdited = true;
                UpdateCategoryFromName();
                UpdateConflictState();
            }
        }
    }

    public string Destination
    {
        get => _destination;
        set
        {
            if (SetProperty(ref _destination, value))
            {
                _destinationEdited = true;
                UpdateConflictState();
                OnPropertyChanged(nameof(FreeSpaceText));
            }
        }
    }

    public Choice<DownloadCategory> Category
    {
        get => _category;
        set
        {
            if (SetProperty(ref _category, value))
            {
                _categoryEdited = true;
                ApplyCategoryFolder();
            }
        }
    }

    public int Connections
    {
        get => _connections;
        set => SetProperty(ref _connections, Math.Clamp(value, 1, 8));
    }

    public bool Acceleration
    {
        get => _acceleration;
        set => SetProperty(ref _acceleration, value);
    }

    public Choice<long?> SpeedLimit
    {
        get => _speedLimit;
        set => SetProperty(ref _speedLimit, value);
    }

    public Choice<DownloadPriority> Priority
    {
        get => _priority;
        set => SetProperty(ref _priority, value);
    }

    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set => SetProperty(ref _scheduleEnabled, value);
    }

    public string ScheduleText
    {
        get => _scheduleText;
        set => SetProperty(ref _scheduleText, value);
    }

    public string HeadersText
    {
        get => _headersText;
        set => SetProperty(ref _headersText, value);
    }

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    public bool RememberCredentials
    {
        get => _rememberCredentials;
        set => SetProperty(ref _rememberCredentials, value);
    }

    public string ExpectedSha256
    {
        get => _expectedSha256;
        set => SetProperty(ref _expectedSha256, value);
    }

    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set => SetProperty(ref _showAdvanced, value);
    }

    public bool IsProbing
    {
        get => _isProbing;
        private set => SetProperty(ref _isProbing, value);
    }

    public string ProbeSummary
    {
        get => _probeSummary;
        private set => SetProperty(ref _probeSummary, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string? DuplicateNotice
    {
        get => _duplicateNotice;
        private set => SetProperty(ref _duplicateNotice, value);
    }

    public bool FileExists { get; private set; }

    public bool ConflictRename
    {
        get => _conflictPolicy == ConflictPolicy.Rename;
        set
        {
            if (value)
            {
                SetConflict(ConflictPolicy.Rename);
            }
        }
    }

    public bool ConflictReplace
    {
        get => _conflictPolicy == ConflictPolicy.Replace;
        set
        {
            if (value)
            {
                SetConflict(ConflictPolicy.Replace);
            }
        }
    }

    public bool ConflictSkip
    {
        get => _conflictPolicy == ConflictPolicy.Skip;
        set
        {
            if (value)
            {
                SetConflict(ConflictPolicy.Skip);
            }
        }
    }

    public string FreeSpaceText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_destination) || !Path.IsPathFullyQualified(_destination))
            {
                return "Enter a full folder path.";
            }

            var free = _fileSystem.GetAvailableSpace(_destination);
            var suffix = Directory.Exists(_destination) ? "" : " · folder will be created";
            return free is { } bytes ? ByteFormatter.FormatSize(bytes) + " free" + suffix : "Drive not available";
        }
    }

    public bool IsHttpWarning => Uri.TryCreate(_url.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && !string.IsNullOrEmpty(_userName);

    private bool CanSubmit => UrlError is null && !string.IsNullOrWhiteSpace(_url);

    private void SetConflict(ConflictPolicy policy)
    {
        _conflictPolicy = policy;
        OnPropertyChanged(nameof(ConflictRename));
        OnPropertyChanged(nameof(ConflictReplace));
        OnPropertyChanged(nameof(ConflictSkip));
    }

    private void ValidateUrl()
    {
        if (string.IsNullOrWhiteSpace(_url))
        {
            UrlError = null;
            DuplicateNotice = null;
            return;
        }

        UrlError = UrlValidator.TryValidate(_url, out var uri, out var error) ? null : error;
        IsMediaPage = uri is not null && MediaUrlDetector.IsMediaPage(uri);
        IsWebPage = false;
        DuplicateNotice = uri is not null && _coordinator.FindActiveDuplicate(uri) is { } dup
            ? $"This link is already in your list ({dup.Status.ToDisplayString()}: {dup.FileName})."
            : null;

        if (uri is not null && !_fileNameEdited)
        {
            _fileName = FileNameSanitizer.FromUrl(uri);
            OnPropertyChanged(nameof(FileName));
            UpdateCategoryFromName();
            UpdateConflictState();
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private void ScheduleProbe()
    {
        _probeCts?.Cancel();
        _probe = null;
        ProbeSummary = "";
        if (UrlError is not null || !UrlValidator.TryValidate(_url, out var uri, out _))
        {
            return;
        }

        if (MediaUrlDetector.IsMediaPage(uri!))
        {
            ProbeSummary = "This is a video page. Continue to choose the video quality.";
            return;
        }

        var cts = new CancellationTokenSource();
        _probeCts = cts;
        _ = ProbeAsync(uri!, cts.Token);
    }

    private async Task ProbeAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);
            IsProbing = true;
            ProbeSummary = "Checking link…";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var result = await _coordinator.ProbeAsync(uri, ParseHeadersOrEmpty(), BuildCredentials(), timeout.Token);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            _probe = result;
            if (!_fileNameEdited && !string.IsNullOrEmpty(result.SuggestedFileName))
            {
                _fileName = result.SuggestedFileName;
                OnPropertyChanged(nameof(FileName));
                UpdateCategoryFromName();
                UpdateConflictState();
            }

            IsWebPage = result.ContentType is "text/html" or "application/xhtml+xml";
            var size = result.ContentLength is { } length ? ByteFormatter.FormatSize(length) : "Unknown size";
            var resume = result.SupportsRanges ? "resumable, multi-connection" : "single connection, cannot resume";
            ProbeSummary = $"{size} · {resume}" + (result.FinalUrl.Host != uri.Host ? $" · via {result.FinalUrl.Host}" : "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ProbeSummary = "Could not check the link: " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsProbing = false;
            }
        }
    }

    private void UpdateCategoryFromName()
    {
        if (_categoryEdited)
        {
            return;
        }

        var classifier = new CategoryClassifier(_settings.Current.CategoryExtensions);
        var category = classifier.Classify(_fileName);
        _category = CategoryChoices.First(c => c.Value == category);
        OnPropertyChanged(nameof(Category));
        ApplyCategoryFolder();
    }

    /// <summary>With auto-organize on, suggests the category folder unless the user picked a folder.</summary>
    private void ApplyCategoryFolder()
    {
        var settings = _settings.Current;
        if (_destinationEdited || !settings.AutoOrganize)
        {
            return;
        }

        var root = string.IsNullOrWhiteSpace(settings.DefaultDownloadDirectory) ? _fileSystem.GetDefaultDownloadDirectory() : settings.DefaultDownloadDirectory;
        _destination = settings.CategoryFolders.TryGetValue(_category.Value, out var folder) && !string.IsNullOrWhiteSpace(folder)
            ? folder
            : _category.Value == DownloadCategory.Other ? root : Path.Combine(root, _category.Value.ToString());
        OnPropertyChanged(nameof(Destination));
        OnPropertyChanged(nameof(FreeSpaceText));
        UpdateConflictState();
    }

    private void UpdateConflictState()
    {
        try
        {
            FileExists = !string.IsNullOrWhiteSpace(_fileName) && File.Exists(Path.Combine(_destination, FileNameSanitizer.Sanitize(_fileName)));
        }
        catch (ArgumentException)
        {
            FileExists = false;
        }

        OnPropertyChanged(nameof(FileExists));
    }

    private void Browse()
    {
        if (_dialogs.PickFolder(_destination) is { } folder)
        {
            Destination = folder;
        }
    }

    private IReadOnlyDictionary<string, string> ParseHeadersOrEmpty() =>
        HeaderValidator.TryParse(_headersText, out var headers, out _) ? headers : new Dictionary<string, string>();

    private DownloadCredentials? BuildCredentials()
    {
        if (string.IsNullOrWhiteSpace(_userName))
        {
            return null;
        }

        return new DownloadCredentials(_userName.Trim(), PasswordProvider?.Invoke() ?? "");
    }

    private void OpenAsMedia()
    {
        if (UrlValidator.TryValidate(_url, out var uri, out _))
        {
            MediaRequestedUrl = uri!.AbsoluteUri;
            _probeCts?.Cancel();
            CloseRequested?.Invoke(this, false);
        }
    }

    private async Task SubmitAsync(bool startNow)
    {
        ErrorMessage = null;
        if (!UrlValidator.TryValidate(_url, out var uri, out var urlError))
        {
            ErrorMessage = urlError;
            return;
        }

        if (MediaUrlDetector.IsMediaPage(uri!))
        {
            // Never save a video page's HTML by accident; pick the video quality instead.
            OpenAsMedia();
            return;
        }

        if (!HeaderValidator.TryParse(_headersText, out var headers, out var headerError))
        {
            ErrorMessage = "Custom headers: " + headerError;
            ShowAdvanced = true;
            return;
        }

        var sha = _expectedSha256.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (sha.Length > 0 && (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)))
        {
            ErrorMessage = "The expected SHA-256 checksum must be 64 hexadecimal characters.";
            ShowAdvanced = true;
            return;
        }

        DateTimeOffset? scheduledAt = null;
        if (_scheduleEnabled)
        {
            if (!DateTime.TryParseExact(_scheduleText.Trim(), ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            {
                ErrorMessage = "Enter the start time as YYYY-MM-DD HH:MM (24-hour).";
                return;
            }

            scheduledAt = new DateTimeOffset(local);
            if (scheduledAt <= DateTimeOffset.Now)
            {
                ErrorMessage = "The scheduled start time must be in the future.";
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(_destination) || !Path.IsPathFullyQualified(_destination))
        {
            ErrorMessage = "Choose a destination folder.";
            return;
        }

        if (DuplicateNotice is not null && !_dialogs.Confirm("Duplicate download", DuplicateNotice + "\n\nAdd it again anyway?", "Add anyway"))
        {
            return;
        }

        var name = FileNameSanitizer.Sanitize(_fileName);
        if (FileExists && _conflictPolicy == ConflictPolicy.Replace &&
            !_dialogs.Confirm("Replace file", $"\"{name}\" already exists in this folder. Replace it when the download finishes?", "Replace"))
        {
            return;
        }

        var credentials = BuildCredentials();
        if (_rememberCredentials && (credentials is not null || headers.Keys.Any(HeaderValidator.IsSensitive)) &&
            !_dialogs.Confirm("Save credentials", "The password and sensitive headers will be stored in Windows Credential Manager for your user account so this download can resume after a restart. Continue?", "Save"))
        {
            return;
        }

        var request = new DownloadRequest
        {
            Url = uri!,
            PageUrl = _pageUrl,
            FileName = _fileNameEdited || _probe?.SuggestedFileName is not null ? name : null,
            DestinationDirectory = _destination.Trim(),
            Category = _category.Value,
            Priority = _priority.Value,
            Source = _source,
            StartNow = startNow && scheduledAt is null,
            ScheduledAt = scheduledAt,
            MaxConnections = _connections,
            AccelerationEnabled = _acceleration,
            SpeedLimit = _speedLimit.Value,
            ConflictPolicy = _conflictPolicy,
            ExpectedSha256 = sha.Length > 0 ? sha : null,
            Headers = headers,
            Credentials = credentials,
            RememberSecrets = _rememberCredentials,
            KnownSize = _probe?.ContentLength,
        };

        var result = await _coordinator.AddAsync(request);
        if (!result.Accepted)
        {
            ErrorMessage = result.Error;
            return;
        }

        _probeCts?.Cancel();
        CloseRequested?.Invoke(this, true);
    }
}
