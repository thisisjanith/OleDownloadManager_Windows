using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OleDownloadManager.App.Mvvm;
using OleDownloadManager.App.Services;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Validation;
using OleDownloadManager.Infrastructure.Media;

namespace OleDownloadManager.App.ViewModels;

public sealed class MediaEntryViewModel(MediaEntry entry) : ObservableObject
{
    private bool _isSelected = true;

    public MediaEntry Entry { get; } = entry;
    public string Title => Entry.Title;
    public string IndexText => Entry.Index.ToString(CultureInfo.CurrentCulture);
    public string DurationText => Entry.Duration is { } d ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : d.ToString(@"m\:ss", CultureInfo.InvariantCulture)) : "";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>A quality in the media dialog, shown with its approximate size when known.</summary>
public sealed record QualityChoice(MediaQuality Quality, string Display)
{
    public override string ToString() => Display;

    public static QualityChoice From(MediaQuality quality) => new(quality,
        quality.ApproximateBytes is { } bytes ? $"{quality.Label}  ·  ~{Core.Formatting.ByteFormatter.FormatSize(bytes)}" : quality.Label);
}

/// <summary>
/// Optional media/playlist dialog backed by a user-installed yt-dlp. Lists the entries of a page or
/// playlist so the user can pick which to download; each becomes a normal queued download.
/// </summary>
public sealed class MediaViewModel : ObservableObject
{
    public const string RightsNotice =
        "Only download media you have the right to save — for example your own uploads, content under a licence that " +
        "permits downloading, or content you have permission to download. Many sites' terms restrict downloading. " +
        "DRM-protected media is not supported.";

    private readonly IDownloadCoordinator _coordinator;
    private readonly ISettingsStore _settings;
    private readonly IMediaTool _tool;
    private readonly IDialogService _dialogs;
    private readonly IFileSystemService _fileSystem;
    private CancellationTokenSource? _inspectCts;
    private string _url;
    private string _destination;
    private QualityChoice _format;
    private string? _ffmpegNote;
    private bool _isBusy;
    private string _status = "";
    private string? _error;
    private string _title = "";
    private bool _acknowledged;
    private bool _toolAvailable = true;

    public MediaViewModel(IDownloadCoordinator coordinator, ISettingsStore settings, IMediaTool tool, IDialogService dialogs, IFileSystemService fileSystem, string? url)
    {
        _coordinator = coordinator;
        _settings = settings;
        _tool = tool;
        _dialogs = dialogs;
        _fileSystem = fileSystem;
        _url = url ?? "";
        var current = settings.Current;
        _acknowledged = current.MediaNoticeAcknowledged;
        _destination = string.IsNullOrWhiteSpace(current.DefaultDownloadDirectory) ? fileSystem.GetDefaultDownloadDirectory() : current.DefaultDownloadDirectory;
        SetQualities(MediaFormats.Standard);
        _format = Qualities[0];

        InspectCommand = new AsyncRelayCommand(InspectAsync, () => _acknowledged && !_isBusy && !string.IsNullOrWhiteSpace(_url));
        BrowseCommand = new RelayCommand(() =>
        {
            if (_dialogs.PickFolder(_destination) is { } folder)
            {
                Destination = folder;
            }
        });
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        AddNowCommand = new AsyncRelayCommand(() => AddAsync(startNow: true), () => _acknowledged && Entries.Any(e => e.IsSelected));
        AddToQueueCommand = new AsyncRelayCommand(() => AddAsync(startNow: false), () => _acknowledged && Entries.Any(e => e.IsSelected));
        AcknowledgeCommand = new AsyncRelayCommand(AcknowledgeAsync);
        UpdateToolCommand = new AsyncRelayCommand(UpdateToolAsync, () => !_isBusy && _toolAvailable);
        _ = StartAsync();
    }

    public event EventHandler? CloseRequested;

    /// <summary>Qualities for the current page (real resolutions once a single video has been read).</summary>
    public ObservableCollection<QualityChoice> Qualities { get; } = [];

    public ObservableCollection<MediaEntryViewModel> Entries { get; } = [];
    public ICommand InspectCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand AddNowCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand AcknowledgeCommand { get; }
    public ICommand UpdateToolCommand { get; }
    public string Notice => RightsNotice;

    public bool Acknowledged
    {
        get => _acknowledged;
        private set => SetProperty(ref _acknowledged, value);
    }

    public bool ToolAvailable
    {
        get => _toolAvailable;
        private set => SetProperty(ref _toolAvailable, value);
    }

    public string Url
    {
        get => _url;
        set => SetProperty(ref _url, value);
    }

    public string Destination
    {
        get => _destination;
        set => SetProperty(ref _destination, value);
    }

    public QualityChoice Format
    {
        get => _format;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _format, value);
            }
        }
    }

    /// <summary>Shown when ffmpeg is missing, which limits the available qualities.</summary>
    public string? FfmpegNote
    {
        get => _ffmpegNote;
        private set => SetProperty(ref _ffmpegNote, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            SetProperty(ref _isBusy, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public string ListTitle
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string SelectionSummary => $"{Entries.Count(e => e.IsSelected)} of {Entries.Count} selected";

    public void CancelPending() => _inspectCts?.Cancel();

    /// <summary>Checks the tool, then reads the page right away when the dialog was opened with a link.</summary>
    private async Task StartAsync()
    {
        var status = await _tool.GetStatusAsync();
        ToolAvailable = status.IsAvailable;
        if (!status.IsAvailable)
        {
            Error = status.Message;
            return;
        }

        Status = $"Using yt-dlp {status.Version}";
        if (!status.FfmpegAvailable)
        {
            FfmpegNote = "ffmpeg was not found, so only qualities available as a single file are offered (often 360p). " +
                         "Install ffmpeg (winget install Gyan.FFmpeg) for HD, 4K and MP3.";
            SetQualities([new("Best available (single file)", MediaFormats.BestSingleFile, null, false), new("Audio only (original format)", MediaFormats.AudioOriginal, null, true)]);
        }

        if (_acknowledged && !string.IsNullOrWhiteSpace(_url))
        {
            await InspectAsync();
        }
    }

    private async Task UpdateToolAsync()
    {
        IsBusy = true;
        Status = "Updating yt-dlp… this can take a minute.";
        try
        {
            var status = await _tool.UpdateAsync();
            Status = $"{status.Message} Version {status.Version}.";
            Error = null;
        }
        catch (Exception ex)
        {
            Error = "Update failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetQualities(IEnumerable<MediaQuality> qualities)
    {
        Qualities.Clear();
        foreach (var quality in qualities)
        {
            Qualities.Add(QualityChoice.From(quality));
        }

        var preferred = _settings.Current.MediaDefaultFormat;
        Format = Qualities.FirstOrDefault(q => q.Quality.Selector == preferred) ?? Qualities[0];
    }

    private async Task AcknowledgeAsync()
    {
        await _settings.UpdateAsync(s => s.MediaNoticeAcknowledged = true);
        Acknowledged = true;
        CommandManager.InvalidateRequerySuggested();
        if (!string.IsNullOrWhiteSpace(_url) && _toolAvailable)
        {
            await InspectAsync();
        }
    }

    private async Task InspectAsync()
    {
        Error = null;
        if (!UrlValidator.TryValidate(_url, out var uri, out var urlError))
        {
            Error = urlError;
            return;
        }

        _inspectCts?.Cancel();
        _inspectCts = new CancellationTokenSource();
        IsBusy = true;
        Status = "Reading the page with yt-dlp…";
        Entries.Clear();
        try
        {
            var inspection = await _tool.InspectAsync(uri!, _inspectCts.Token);
            ListTitle = inspection.IsPlaylist ? $"{inspection.Title} — playlist" : inspection.Title;
            foreach (var entry in inspection.Entries)
            {
                var vm = new MediaEntryViewModel(entry);
                vm.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectionSummary));
                Entries.Add(vm);
            }

            SetQualities(inspection.Qualities);
            Status = inspection.Entries.Count == 0
                ? "No downloadable entries were found."
                : inspection.IsPlaylist
                    ? $"Found {inspection.Entries.Count} item(s). Choose the maximum quality for the whole playlist."
                    : $"Choose a quality ({Qualities.Count(q => !q.Quality.AudioOnly)} video resolutions available).";
            OnPropertyChanged(nameof(SelectionSummary));
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetAll(bool selected)
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = selected;
        }
    }

    private async Task AddAsync(bool startNow)
    {
        var selected = Entries.Where(e => e.IsSelected).ToList();
        var audioOnly = _format.Quality.AudioOnly;
        var added = 0;
        string? lastError = null;
        foreach (var entry in selected)
        {
            var result = await _coordinator.AddAsync(new DownloadRequest
            {
                Url = entry.Entry.Url,
                Kind = DownloadKind.Media,
                FileName = FileNameSanitizer.Sanitize(entry.Title, "media"),
                DestinationDirectory = _destination,
                Category = audioOnly ? DownloadCategory.Audio : DownloadCategory.Video,
                MediaFormat = _format.Quality.Selector,
                Source = DownloadSource.MediaTool,
                StartNow = startNow,
                AccelerationEnabled = false,
            });
            if (result.Accepted)
            {
                added++;
            }
            else
            {
                lastError = result.Error;
            }
        }

        if (added == selected.Count)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Error = $"Added {added} of {selected.Count}. {lastError}";
        }
    }
}

/// <summary>Small dialog for choosing a start time.</summary>
public sealed class ScheduleViewModel : ObservableObject
{
    private string _text;
    private string? _error;

    public ScheduleViewModel(DateTimeOffset? current)
    {
        _text = (current?.ToLocalTime().DateTime ?? DateTime.Now.AddHours(1)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public DateTimeOffset? Result { get; private set; }

    public bool TryAccept()
    {
        if (!DateTime.TryParseExact(_text.Trim(), ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            Error = "Use the format YYYY-MM-DD HH:MM (24-hour).";
            return false;
        }

        var value = new DateTimeOffset(local);
        if (value <= DateTimeOffset.Now)
        {
            Error = "Choose a time in the future.";
            return false;
        }

        Result = value;
        return true;
    }
}
