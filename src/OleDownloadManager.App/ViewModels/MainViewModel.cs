using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using OleDownloadManager.App.Mvvm;
using OleDownloadManager.App.Services;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Formatting;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.App.ViewModels;

/// <summary>
/// Main window state: navigation, the filtered/sorted download list, selection and all user commands.
/// Engine updates arrive on background threads and are coalesced into a 250 ms UI refresh.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDownloadCoordinator _coordinator;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly Dictionary<Guid, DownloadItemViewModel> _byId = [];
    private readonly ConcurrentDictionary<Guid, DownloadSnapshot> _pending = new();
    private readonly ConcurrentQueue<Guid> _removed = new();
    private readonly DispatcherTimer _refreshTimer;
    private IReadOnlyList<DownloadItemViewModel> _selection = [];
    private NavItemViewModel _currentNav;
    private string _searchText = "";
    private string _statusMessage = "Ready";
    private string _sortColumn;
    private bool _sortDescending;
    private bool _detailsVisible;
    private DownloadItemViewModel? _selectedItem;
    private int _activeCount;
    private string _totalSpeedText = "";
    private bool _queueRunning;

    public MainViewModel(IDownloadCoordinator coordinator, ISettingsStore settings, IDialogService dialogs, IShellService shell)
    {
        _coordinator = coordinator;
        _settings = settings;
        _dialogs = dialogs;
        _shell = shell;

        Sections =
        [
            new(NavKind.All, "All Downloads", Glyphs.All),
            new(NavKind.Downloading, "Downloading", Glyphs.Download),
            new(NavKind.Queue, "Queue", Glyphs.List),
            new(NavKind.Paused, "Paused", Glyphs.Pause),
            new(NavKind.Completed, "Completed", Glyphs.CheckMark),
            new(NavKind.Failed, "Failed", Glyphs.Error),
            new(NavKind.Scheduled, "Scheduled", Glyphs.Clock),
            new(NavKind.Trash, "Trash", Glyphs.Delete),
        ];
        Categories =
        [
            new(NavKind.Category, "Applications", Glyphs.Apps, DownloadCategory.Applications),
            new(NavKind.Category, "Documents", Glyphs.Document, DownloadCategory.Documents),
            new(NavKind.Category, "Archives", Glyphs.Archive, DownloadCategory.Archives),
            new(NavKind.Category, "Video", Glyphs.Video, DownloadCategory.Video),
            new(NavKind.Category, "Audio", Glyphs.Audio, DownloadCategory.Audio),
            new(NavKind.Category, "Images", Glyphs.Image, DownloadCategory.Images),
            new(NavKind.Category, "Other", Glyphs.Page, DownloadCategory.Other),
        ];
        foreach (var nav in Sections.Concat(Categories))
        {
            nav.Selected += OnNavSelected;
        }

        _currentNav = Sections[0];
        _currentNav.IsSelected = true;

        var current = settings.Current;
        _sortColumn = current.SortColumn;
        _sortDescending = current.SortDescending;
        _detailsVisible = current.DetailsPanelVisible;

        View = new ListCollectionView(Items)
        {
            Filter = FilterItem,
            IsLiveFiltering = true,
            IsLiveSorting = true,
        };
        foreach (var property in new[] { nameof(DownloadItemViewModel.Status), nameof(DownloadItemViewModel.IsDeleted), nameof(DownloadItemViewModel.Category), nameof(DownloadItemViewModel.IsScheduled) })
        {
            View.LiveFilteringProperties.Add(property);
        }

        ApplySort();

        AddDownloadCommand = new RelayCommand(() => AddDownload(null));
        PasteUrlCommand = new RelayCommand(PasteUrl);
        AddMediaCommand = new RelayCommand(() => _dialogs.ShowMediaDialog(null));
        StartQueueCommand = new AsyncRelayCommand(() => _coordinator.StartQueueAsync(), () => !QueueRunning);
        PauseQueueCommand = new AsyncRelayCommand(() => _coordinator.StopQueueAsync(), () => QueueRunning);
        PauseAllCommand = new AsyncRelayCommand(() => _coordinator.PauseAllAsync());
        ResumeAllCommand = new AsyncRelayCommand(() => _coordinator.ResumeAllAsync());
        SettingsCommand = new RelayCommand(() => _dialogs.ShowSettings());
        AboutCommand = new RelayCommand(() => _dialogs.ShowAbout());
        ToggleDetailsCommand = new RelayCommand(() => DetailsVisible = !DetailsVisible);
        SortCommand = new RelayCommand(p => SortBy(p as string));
        FocusSearchCommand = new RelayCommand(() => SearchFocusRequested?.Invoke(this, EventArgs.Empty));

        StartCommand = new AsyncRelayCommand(() => _coordinator.StartAsync(Ids(i => i.CanStart || i.CanRetry)), () => _selection.Any(i => i.CanStart || i.CanRetry));
        PauseCommand = new AsyncRelayCommand(PauseSelectedAsync, () => _selection.Any(i => i.CanPause));
        TogglePauseCommand = new AsyncRelayCommand(TogglePauseAsync, () => _selection.Any(i => i.CanPause || i.CanStart));
        RetryCommand = new AsyncRelayCommand(() => _coordinator.RetryAsync(Ids(i => i.CanRetry)), () => _selection.Any(i => i.CanRetry));
        CancelCommand = new AsyncRelayCommand(CancelSelectedAsync, () => _selection.Any(i => i.CanCancel));
        RedownloadCommand = new AsyncRelayCommand(() => _coordinator.RedownloadAsync(Ids(i => i.CanRedownload)), () => _selection.Any(i => i.CanRedownload));
        RemoveCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => _selection.Count > 0);
        RestoreCommand = new AsyncRelayCommand(() => _coordinator.RestoreFromTrashAsync(Ids(i => i.IsDeleted)), () => _selection.Any(i => i.IsDeleted));
        EmptyTrashCommand = new AsyncRelayCommand(EmptyTrashAsync, () => Items.Any(i => i.IsDeleted));
        DeleteIncompleteCommand = new AsyncRelayCommand(DeleteIncompleteAsync, () => _selection.Any(i => i.CanDeleteIncompleteData));
        OpenFileCommand = new RelayCommand(OpenSelectedFile, () => SelectedItem?.CanOpen == true);
        RevealCommand = new RelayCommand(RevealSelected, () => SelectedItem?.CanReveal == true);
        CopyUrlCommand = new RelayCommand(() => _shell.CopyText(string.Join(Environment.NewLine, _selection.Select(i => i.SourceUrl))), () => _selection.Count > 0);
        CopyPathCommand = new RelayCommand(() => _shell.CopyText(string.Join(Environment.NewLine, _selection.Select(i => i.DestinationPath))), () => _selection.Count > 0);
        MoveUpCommand = new AsyncRelayCommand(() => _coordinator.MoveInQueueAsync(SelectedItem!.Id, -1), () => SelectedItem is { IsDeleted: false } s && s.Status != DownloadStatus.Completed);
        MoveDownCommand = new AsyncRelayCommand(() => _coordinator.MoveInQueueAsync(SelectedItem!.Id, 1), () => SelectedItem is { IsDeleted: false } s && s.Status != DownloadStatus.Completed);
        SetPriorityCommand = new AsyncRelayCommand(p => _coordinator.SetPriorityAsync(Ids(_ => true), Enum.Parse<DownloadPriority>((string)p!)), _ => _selection.Count > 0);
        ScheduleCommand = new AsyncRelayCommand(ScheduleSelectedAsync, () => _selection.Any(i => i.Status is DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Interrupted or DownloadStatus.Failed));
        SetSpeedLimitCommand = new AsyncRelayCommand(p => _coordinator.SetSpeedLimitAsync(Ids(_ => true), p is long l && l > 0 ? l : null), _ => _selection.Count > 0);
        RowActionCommand = new AsyncRelayCommand(p => RowActionAsync(p as DownloadItemViewModel));

        _coordinator.DownloadUpdated += OnDownloadUpdated;
        _coordinator.DownloadRemoved += OnDownloadRemoved;
        _coordinator.QueueStateChanged += OnQueueStateChanged;
        _queueRunning = _coordinator.IsQueueRunning;

        foreach (var snapshot in _coordinator.GetAll().OrderBy(s => s.CreatedAt))
        {
            var vm = new DownloadItemViewModel(snapshot);
            _byId[snapshot.Id] = vm;
            Items.Add(vm);
        }

        UpdateCounts();
        UpdateStatusBar();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => ApplyPendingUpdates();
        _refreshTimer.Start();
    }

    public event EventHandler? SearchFocusRequested;

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];
    public ListCollectionView View { get; }
    public IReadOnlyList<NavItemViewModel> Sections { get; }
    public IReadOnlyList<NavItemViewModel> Categories { get; }

    public NavItemViewModel CurrentNav => _currentNav;
    public string ListTitle => _currentNav.Title;
    public bool IsTrashView => _currentNav.Kind == NavKind.Trash;
    public bool IsListEmpty => View.IsEmpty;

    public string EmptyText => !string.IsNullOrWhiteSpace(_searchText)
        ? "No downloads match your search."
        : _currentNav.Kind switch
        {
            NavKind.All => "No downloads yet. Press Ctrl+N or paste a link to start.",
            NavKind.Trash => "Trash is empty.",
            _ => $"Nothing in {_currentNav.Title}.",
        };

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshView();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool DetailsVisible
    {
        get => _detailsVisible;
        set
        {
            if (SetProperty(ref _detailsVisible, value))
            {
                _ = _settings.UpdateAsync(s => s.DetailsPanelVisible = value);
            }
        }
    }

    public DownloadItemViewModel? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => _selectedItem is not null;
    public IReadOnlyList<DownloadItemViewModel> Selection => _selection;

    public int ActiveCount
    {
        get => _activeCount;
        private set => SetProperty(ref _activeCount, value);
    }

    public string TotalSpeedText
    {
        get => _totalSpeedText;
        private set => SetProperty(ref _totalSpeedText, value);
    }

    public bool QueueRunning
    {
        get => _queueRunning;
        private set
        {
            if (SetProperty(ref _queueRunning, value))
            {
                OnPropertyChanged(nameof(QueueStatusText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string QueueStatusText => _queueRunning ? "Queue running" : "Queue paused";

    public string SpeedLimitText => _settings.Current.GlobalSpeedLimit is { } limit ? "Limit " + ByteFormatter.FormatSpeed(limit) : "No speed limit";

    public string SortColumn => _sortColumn;
    public bool SortDescending => _sortDescending;

    public ICommand AddDownloadCommand { get; }
    public ICommand PasteUrlCommand { get; }
    public ICommand AddMediaCommand { get; }
    public ICommand StartQueueCommand { get; }
    public ICommand PauseQueueCommand { get; }
    public ICommand PauseAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand ToggleDetailsCommand { get; }
    public ICommand SortCommand { get; }
    public ICommand FocusSearchCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RedownloadCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand EmptyTrashCommand { get; }
    public ICommand DeleteIncompleteCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand SetPriorityCommand { get; }
    public ICommand ScheduleCommand { get; }
    public ICommand SetSpeedLimitCommand { get; }
    public ICommand RowActionCommand { get; }

    /// <summary>Called by the view when the list selection changes.</summary>
    public void UpdateSelection(IEnumerable<DownloadItemViewModel> selected)
    {
        _selection = selected.ToList();
        SelectedItem = _selection.FirstOrDefault();
        OnPropertyChanged(nameof(Selection));
        CommandManager.InvalidateRequerySuggested();
    }

    public void AddDownload(AddDownloadPrefill? prefill)
    {
        // Video pages (YouTube etc.) are not files: downloading the address would only save the HTML page.
        if (MediaUrlDetector.IsMediaPage(prefill?.Url))
        {
            _dialogs.ShowMediaDialog(prefill!.Url);
            return;
        }

        if (_dialogs.ShowAddDownload(prefill))
        {
            StatusMessage = "Download added.";
        }
    }

    /// <summary>Adds URLs dropped onto the window or pasted with Ctrl+V.</summary>
    public void AddFromText(string? text)
    {
        var urls = (text ?? "")
            .Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Contains("://", StringComparison.Ordinal) && UrlValidator.IsValid(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (urls.Count == 0)
        {
            StatusMessage = "The clipboard does not contain an HTTP or HTTPS link.";
            return;
        }

        if (urls.Count == 1)
        {
            AddDownload(new AddDownloadPrefill(urls[0]));
            return;
        }

        var videoPages = urls.Where(MediaUrlDetector.IsMediaPage).ToList();
        var files = urls.Except(videoPages).ToList();
        if (files.Count == 0)
        {
            // Several video links: the media dialog handles one page or playlist at a time.
            _dialogs.ShowMediaDialog(videoPages[0]);
            return;
        }

        var note = videoPages.Count > 0 ? $"\n\n{videoPages.Count} video page link(s) will be skipped; add them with Media (Ctrl+M)." : "";
        if (_dialogs.Confirm("Add links", $"Add {files.Count} links to the queue?{note}", "Add to Queue"))
        {
            _ = AddManyAsync(files);
        }
    }

    private async Task AddManyAsync(IReadOnlyList<string> urls)
    {
        var added = 0;
        foreach (var url in urls)
        {
            if (UrlValidator.TryValidate(url, out var uri, out _) && (await _coordinator.AddAsync(new DownloadRequest { Url = uri!, StartNow = false })).Accepted)
            {
                added++;
            }
        }

        StatusMessage = $"Added {added} of {urls.Count} links to the queue.";
    }

    private void PasteUrl() => AddFromText(_shell.GetClipboardText());

    private IReadOnlyList<Guid> Ids(Func<DownloadItemViewModel, bool> predicate) => _selection.Where(predicate).Select(i => i.Id).ToList();

    private async Task PauseSelectedAsync()
    {
        var targets = _selection.Where(i => i.CanPause).ToList();
        var nonResumable = targets.Where(i => i.Status.IsActive() && i.Snapshot.SupportsRangeRequests == false && i.Snapshot.DownloadedBytes > 0).ToList();
        if (nonResumable.Count > 0 && !_dialogs.Confirm("Pause download",
                $"The server for \"{nonResumable[0].FileName}\" does not support resuming. If you pause, the download will start over from the beginning.\n\nPause anyway?", "Pause"))
        {
            return;
        }

        StatusMessage = "Pausing…";
        await _coordinator.PauseAsync(targets.Select(i => i.Id));
        StatusMessage = targets.Count == 1 ? $"Paused {targets[0].FileName}." : $"Paused {targets.Count} downloads.";
    }

    private Task TogglePauseAsync()
    {
        if (_selection.Any(i => i.CanPause && (i.Status.IsActive() || i.Status == DownloadStatus.Queued)))
        {
            return PauseSelectedAsync();
        }

        return _coordinator.StartAsync(Ids(i => i.CanStart));
    }

    private async Task CancelSelectedAsync()
    {
        var targets = _selection.Where(i => i.CanCancel).ToList();
        var withData = targets.Count(i => i.Snapshot.HasPartialData);
        if (withData > 0 && !_dialogs.Confirm("Cancel download",
                $"Cancel {(targets.Count == 1 ? $"\"{targets[0].FileName}\"" : $"{targets.Count} downloads")}? Downloaded partial data will be deleted.", "Cancel download"))
        {
            return;
        }

        await _coordinator.CancelAsync(targets.Select(i => i.Id));
    }

    private async Task RemoveSelectedAsync()
    {
        var targets = _selection.ToList();
        if (targets.All(i => i.IsDeleted))
        {
            if (_dialogs.Confirm("Delete permanently", $"Permanently remove {targets.Count} item(s) from history? Partial data will be deleted. Completed files on disk are kept.", "Delete"))
            {
                await _coordinator.DeletePermanentlyAsync(targets.Select(i => i.Id));
            }

            return;
        }

        var active = targets.Count(i => i.Status.IsActive());
        if (_settings.Current.ConfirmBeforeRemove || active > 0)
        {
            var message = active > 0
                ? $"{active} of the selected downloads are in progress and will be paused. Move {targets.Count} item(s) to Trash?"
                : $"Move {targets.Count} item(s) to Trash? Files on disk are not deleted.";
            if (!_dialogs.Confirm("Remove from list", message, "Move to Trash"))
            {
                return;
            }
        }

        await _coordinator.MoveToTrashAsync(targets.Select(i => i.Id));
        StatusMessage = $"Moved {targets.Count} item(s) to Trash.";
    }

    private async Task EmptyTrashAsync()
    {
        var trashed = Items.Where(i => i.IsDeleted).Select(i => i.Id).ToList();
        if (trashed.Count > 0 && _dialogs.Confirm("Empty Trash", $"Permanently remove {trashed.Count} item(s)? Completed files on disk are kept; partial data is deleted.", "Empty Trash"))
        {
            await _coordinator.DeletePermanentlyAsync(trashed);
        }
    }

    private async Task DeleteIncompleteAsync()
    {
        var targets = Ids(i => i.CanDeleteIncompleteData);
        if (_dialogs.Confirm("Delete incomplete data", $"Delete partial data for {targets.Count} download(s)? They will start from the beginning next time.", "Delete"))
        {
            await _coordinator.DeleteIncompleteDataAsync(targets);
        }
    }

    private async Task ScheduleSelectedAsync()
    {
        var choice = _dialogs.AskSchedule(SelectedItem?.Snapshot.ScheduledAt);
        if (choice.Confirmed)
        {
            await _coordinator.SetScheduleAsync(Ids(i => i.Status is DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Interrupted or DownloadStatus.Failed), choice.When);
        }
    }

    private void OpenSelectedFile()
    {
        if (SelectedItem is { } item && _shell.OpenFile(item.DestinationPath) is { } error)
        {
            _dialogs.ShowError("Open file", error);
        }
    }

    private void RevealSelected()
    {
        if (SelectedItem is { } item && _shell.RevealInExplorer(item.DestinationPath) is { } error)
        {
            _dialogs.ShowError("Show in folder", error);
        }
    }

    private async Task RowActionAsync(DownloadItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.CanOpen)
        {
            if (_shell.OpenFile(item.DestinationPath) is { } error)
            {
                _dialogs.ShowError("Open file", error);
            }
        }
        else if (item.CanRetry)
        {
            await _coordinator.RetryAsync([item.Id]);
        }
        else if (item.CanPause && (item.Status.IsActive() || item.Status == DownloadStatus.Queued))
        {
            UpdateSelection([item]);
            await PauseSelectedAsync();
        }
        else if (item.CanStart)
        {
            await _coordinator.StartAsync([item.Id]);
        }
    }

    private void OnNavSelected(object? sender, EventArgs e)
    {
        if (sender is not NavItemViewModel nav || ReferenceEquals(nav, _currentNav))
        {
            return;
        }

        var previous = _currentNav;
        _currentNav = nav;
        previous.IsSelected = false;
        OnPropertyChanged(nameof(CurrentNav));
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(IsTrashView));
        RefreshView();
    }

    private bool FilterItem(object obj)
    {
        if (obj is not DownloadItemViewModel item || !_currentNav.Matches(item))
        {
            return false;
        }

        var search = _searchText.Trim();
        return search.Length == 0 ||
               item.FileName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               item.HostName.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshView()
    {
        View.Refresh();
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void SortBy(string? column)
    {
        if (string.IsNullOrEmpty(column))
        {
            return;
        }

        if (column == _sortColumn)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = column is "CreatedAt" or "Size" or "Progress";
        }

        ApplySort();
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        var (col, desc) = (_sortColumn, _sortDescending);
        _ = _settings.UpdateAsync(s =>
        {
            s.SortColumn = col;
            s.SortDescending = desc;
        });
    }

    private void ApplySort()
    {
        var property = _sortColumn switch
        {
            "FileName" => nameof(DownloadItemViewModel.FileName),
            "Size" => nameof(DownloadItemViewModel.SortSize),
            "Progress" => nameof(DownloadItemViewModel.SortProgress),
            "Status" => nameof(DownloadItemViewModel.Status),
            _ => nameof(DownloadItemViewModel.CreatedAt),
        };

        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            View.SortDescriptions.Add(new SortDescription(property, _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
            if (property != nameof(DownloadItemViewModel.CreatedAt))
            {
                View.SortDescriptions.Add(new SortDescription(nameof(DownloadItemViewModel.CreatedAt), ListSortDirection.Descending));
            }

            View.LiveSortingProperties.Clear();
            View.LiveSortingProperties.Add(property);
        }
    }

    // ---------------------------------------------------------------- engine updates

    private void OnDownloadUpdated(object? sender, DownloadSnapshot snapshot) => _pending[snapshot.Id] = snapshot;

    private void OnDownloadRemoved(object? sender, Guid id) => _removed.Enqueue(id);

    private void OnQueueStateChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => QueueRunning = _coordinator.IsQueueRunning);

    /// <summary>Applies queued snapshots on the UI thread; also callable directly by tests.</summary>
    public void ApplyPendingUpdates()
    {
        var membershipChanged = false;
        while (_removed.TryDequeue(out var removedId))
        {
            _pending.TryRemove(removedId, out _);
            if (_byId.Remove(removedId, out var vm))
            {
                Items.Remove(vm);
                membershipChanged = true;
            }
        }

        foreach (var id in _pending.Keys.ToList())
        {
            if (!_pending.TryRemove(id, out var snapshot))
            {
                continue;
            }

            if (_byId.TryGetValue(id, out var existing))
            {
                membershipChanged |= existing.Update(snapshot);
            }
            else if (_coordinator.Get(id) is not null)
            {
                var vm = new DownloadItemViewModel(snapshot);
                _byId[id] = vm;
                Items.Add(vm);
                membershipChanged = true;
            }
        }

        if (membershipChanged)
        {
            UpdateCounts();
            OnPropertyChanged(nameof(IsListEmpty));
            OnPropertyChanged(nameof(EmptyText));
            CommandManager.InvalidateRequerySuggested();
        }

        UpdateStatusBar();
    }

    private void UpdateCounts()
    {
        foreach (var nav in Sections.Concat(Categories))
        {
            nav.Count = Items.Count(nav.Matches);
        }
    }

    private void UpdateStatusBar()
    {
        var active = Items.Where(i => i.Status.IsActive()).ToList();
        ActiveCount = active.Count;
        var speed = active.Sum(i => i.Snapshot.CurrentSpeed);
        TotalSpeedText = speed > 0 ? ByteFormatter.FormatSpeed(speed) : "";
        QueueRunning = _coordinator.IsQueueRunning;
        OnPropertyChanged(nameof(SpeedLimitText));
        StatusUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after each refresh so the tray icon can mirror activity.</summary>
    public event EventHandler? StatusUpdated;

    public void Dispose()
    {
        _refreshTimer.Stop();
        _coordinator.DownloadUpdated -= OnDownloadUpdated;
        _coordinator.DownloadRemoved -= OnDownloadRemoved;
        _coordinator.QueueStateChanged -= OnQueueStateChanged;
    }
}
