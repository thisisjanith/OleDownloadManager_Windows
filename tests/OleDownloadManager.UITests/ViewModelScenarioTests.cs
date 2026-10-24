using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using OleDownloadManager.App.Services;
using OleDownloadManager.App.ViewModels;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Infrastructure.FileSystem;
using OleDownloadManager.IntegrationTests.Infrastructure;

namespace OleDownloadManager.UITests;

internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Confirmations { get; } = [];
    public List<string> Errors { get; } = [];
    public Func<AddDownloadPrefill?, bool>? OnAddDownload { get; set; }

    public bool ShowAddDownload(AddDownloadPrefill? prefill) => OnAddDownload?.Invoke(prefill) ?? false;

    public void ShowSettings()
    {
    }

    public List<string?> MediaRequests { get; } = [];

    public void ShowMediaDialog(string? url) => MediaRequests.Add(url);

    public bool Confirm(string title, string message, string confirmText = "OK")
    {
        Confirmations.Add(title);
        return ConfirmAnswer;
    }

    public void ShowError(string title, string message) => Errors.Add($"{title}: {message}");

    public ScheduleChoice AskSchedule(DateTimeOffset? current) => new(true, DateTimeOffset.Now.AddHours(1));

    public string? PickFolder(string? initialDirectory) => null;

    public void ShowAbout()
    {
    }
}

internal sealed class FakeShell : IShellService
{
    public List<string> Opened { get; } = [];
    public List<string> Revealed { get; } = [];
    public string? Clipboard { get; set; }
    public string? Copied { get; private set; }

    public string? OpenFile(string path)
    {
        Opened.Add(path);
        return File.Exists(path) ? null : "missing";
    }

    public string? RevealInExplorer(string path)
    {
        Revealed.Add(path);
        return null;
    }

    public string? OpenFolder(string path) => null;

    public void CopyText(string text) => Copied = text;

    public string? GetClipboardText() => Clipboard;
}

/// <summary>
/// User workflows driven through the real view models, coordinator, SQLite store and local HTTP server.
/// These cover the same behaviour the buttons and menus in the window are bound to.
/// </summary>
public sealed class ViewModelScenarioTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private EngineHarness _harness = null!;
    private FakeDialogs _dialogs = null!;
    private FakeShell _shell = null!;
    private MainViewModel? _mainField;

    private MainViewModel _main
    {
        get => _mainField!;
        set => _mainField = value;
    }

    public async ValueTask InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        _harness = await EngineHarness.CreateAsync(s => s.ConfirmBeforeRemove = true);
        _dialogs = new FakeDialogs();
        _shell = new FakeShell();
    }

    public async ValueTask DisposeAsync()
    {
        _mainField?.Dispose();
        await _harness.DisposeAsync();
        await _server.DisposeAsync();
    }

    /// <summary>
    /// Runs a scenario on a dedicated STA thread with a WPF dispatcher, like the app's UI thread, so
    /// collection views and timers behave as they do in the real window.
    /// </summary>
    private Task OnUiAsync(Func<Task> body)
    {
        var completion = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            _ = RunAsync();
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();

            async Task RunAsync()
            {
                try
                {
                    _main = new MainViewModel(_harness.Coordinator, _harness.Settings, _dialogs, _shell);
                    await body();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
                finally
                {
                    _mainField?.Dispose();
                    _mainField = null;
                    frame.Continue = false;
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private AddDownloadViewModel NewAddDialog(AddDownloadPrefill? prefill = null) =>
        new(_harness.Coordinator, _harness.Settings, new FileSystemService(NullLogger<FileSystemService>.Instance), _dialogs, prefill);

    private async Task<DownloadItemViewModel> WaitForRowAsync(Func<DownloadItemViewModel, bool> condition, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            _main.ApplyPendingUpdates();
            var row = _main.Items.FirstOrDefault(condition);
            if (row is not null)
            {
                return row;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Row condition not met. Rows: " + string.Join(", ", _main.Items.Select(i => $"{i.FileName}={i.Status}")));
    }

    private static async Task ExecuteAsync(ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter), "command should be enabled");
        if (command is OleDownloadManager.App.Mvvm.AsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(parameter);
        }
        else
        {
            command.Execute(parameter);
        }
    }

    [Fact]
    public Task AddingAUrl_ProbesNameAndSize_ThenDownloadsToChosenFolder() => OnUiAsync(async () =>
    {
        var other = Directory.CreateDirectory(Path.Combine(_harness.Root, "other-drive-folder")).FullName;
        var dialog = NewAddDialog(new AddDownloadPrefill(_server.Url("disposition/250000?name=manual.pdf").AbsoluteUri));
        var closed = false;
        dialog.CloseRequested += (_, _) => closed = true;

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (string.IsNullOrEmpty(dialog.ProbeSummary) || dialog.ProbeSummary.StartsWith("Checking", StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < deadline, "probe did not finish");
            await Task.Delay(50);
        }

        Assert.Equal("manual.pdf", dialog.FileName);
        Assert.Equal(DownloadCategory.Documents, dialog.Category.Value);
        Assert.Contains("resumable", dialog.ProbeSummary, StringComparison.Ordinal);

        dialog.Destination = other;
        await ExecuteAsync(dialog.StartNowCommand);
        Assert.True(closed);

        var row = await WaitForRowAsync(r => r.Status == DownloadStatus.Completed);
        Assert.Equal(Path.Combine(other, "manual.pdf"), row.DestinationPath);
        Assert.True(File.Exists(row.DestinationPath));
        Assert.Equal("100%", row.ProgressText);
        Assert.Equal(1, _main.Sections.Single(s => s.Kind == NavKind.Completed).Count);
        Assert.Equal(1, _main.Categories.Single(c => c.Category == DownloadCategory.Documents).Count);
    });

    [Fact]
    public Task InvalidUrl_IsRejectedInTheDialog() => OnUiAsync(async () =>
    {
        var dialog = NewAddDialog();
        dialog.Url = "ftp://example.com/file";
        Assert.NotNull(dialog.UrlError);
        Assert.False(dialog.StartNowCommand.CanExecute(null));
        dialog.Url = "https://example.com/file.zip";
        Assert.Null(dialog.UrlError);
        Assert.Equal("file.zip", dialog.FileName);
        await Task.CompletedTask;
    });

    [Fact]
    public Task PauseAndResume_FromCommands() => OnUiAsync(async () =>
    {
        await _harness.AddAsync(_server.Url("file/3000000?bps=600000"), customize: r => r with { FileName = "big.bin" });
        var row = await WaitForRowAsync(r => r.Status == DownloadStatus.Downloading && r.Snapshot.DownloadedBytes > 200_000);
        _main.UpdateSelection([row]);

        await ExecuteAsync(_main.PauseCommand);
        row = await WaitForRowAsync(r => r.Status == DownloadStatus.Paused);
        Assert.True(row.CanStart);
        Assert.Equal(1, _main.Sections.Single(s => s.Kind == NavKind.Paused).Count);

        _main.UpdateSelection([row]);
        await ExecuteAsync(_main.StartCommand);
        row = await WaitForRowAsync(r => r.Status == DownloadStatus.Completed);
        Assert.Equal(TestContent.Sha256(3000000), TestContent.FileSha256(row.DestinationPath));
    });

    [Fact]
    public Task SpaceToggle_PausesThenResumes() => OnUiAsync(async () =>
    {
        await _harness.AddAsync(_server.Url("file/2000000?bps=400000"));
        var row = await WaitForRowAsync(r => r.Status == DownloadStatus.Downloading);
        _main.UpdateSelection([row]);
        await ExecuteAsync(_main.TogglePauseCommand);
        row = await WaitForRowAsync(r => r.Status == DownloadStatus.Paused);
        _main.UpdateSelection([row]);
        await ExecuteAsync(_main.TogglePauseCommand);
        await WaitForRowAsync(r => r.Status is DownloadStatus.Downloading or DownloadStatus.Completed or DownloadStatus.Connecting);
    });

    [Fact]
    public Task Cancel_AsksForConfirmation_AndRemovesPartialData() => OnUiAsync(async () =>
    {
        await _harness.AddAsync(_server.Url("file/3000000?bps=300000"));
        var row = await WaitForRowAsync(r => r.Snapshot.DownloadedBytes > 100_000);
        _main.UpdateSelection([row]);

        _dialogs.ConfirmAnswer = false;
        await ExecuteAsync(_main.CancelCommand);
        Assert.Contains("Cancel download", _dialogs.Confirmations);
        Assert.NotEqual(DownloadStatus.Canceled, _harness.Coordinator.Get(row.Id)!.Status);

        _dialogs.ConfirmAnswer = true;
        await ExecuteAsync(_main.CancelCommand);
        row = await WaitForRowAsync(r => r.Status == DownloadStatus.Canceled);
        Assert.False(row.Snapshot.HasPartialData);
        Assert.Equal(1, _main.Sections.Single(s => s.Kind == NavKind.Failed).Count);
    });

    [Fact]
    public Task Retry_RestartsAFailedDownload() => OnUiAsync(async () =>
    {
        await _harness.Settings.UpdateAsync(s => s.MaxRetries = 0);
        await _harness.AddAsync(_server.Url("flaky/ui/5000?failures=1"));
        var row = await WaitForRowAsync(r => r.Status == DownloadStatus.Failed);
        Assert.True(row.HasMessage);
        Assert.Equal(Glyphs.Retry, row.PrimaryActionGlyph);

        _main.UpdateSelection([row]);
        await ExecuteAsync(_main.RetryCommand);
        await WaitForRowAsync(r => r.Status == DownloadStatus.Completed);
    });

    [Fact]
    public Task SearchAndNavigationFilterTheList() => OnUiAsync(async () =>
    {
        await _harness.AddAsync(_server.Url("file/1000"), customize: r => r with { FileName = "holiday-video.mp4" });
        await _harness.AddAsync(_server.Url("file/1001"), customize: r => r with { FileName = "invoice.pdf" });
        await WaitForRowAsync(r => r.FileName == "holiday-video.mp4" && r.Status == DownloadStatus.Completed);
        await WaitForRowAsync(r => r.FileName == "invoice.pdf" && r.Status == DownloadStatus.Completed);

        _main.SearchText = "invoice";
        Assert.Equal(["invoice.pdf"], _main.View.Cast<DownloadItemViewModel>().Select(i => i.FileName));
        _main.SearchText = "127.0.0.1";
        Assert.Equal(2, _main.View.Cast<DownloadItemViewModel>().Count());
        _main.SearchText = "";

        _main.Categories.Single(c => c.Category == DownloadCategory.Video).IsSelected = true;
        Assert.Equal(["holiday-video.mp4"], _main.View.Cast<DownloadItemViewModel>().Select(i => i.FileName));
        Assert.Equal("Video", _main.ListTitle);

        _main.Sections.Single(s => s.Kind == NavKind.Failed).IsSelected = true;
        Assert.True(_main.IsListEmpty);
        Assert.False(_main.Categories.Single(c => c.Category == DownloadCategory.Video).IsSelected);
    });

    [Fact]
    public Task OpenFileAndRevealUseTheShell() => OnUiAsync(async () =>
    {
        await _harness.AddAsync(_server.Url("file/1200"), customize: r => r with { FileName = "readme.txt" });
        var row = await WaitForRowAsync(r => r.Status == DownloadStatus.Completed);
        _main.UpdateSelection([row]);

        await ExecuteAsync(_main.OpenFileCommand);
        await ExecuteAsync(_main.RevealCommand);
        await ExecuteAsync(_main.CopyPathCommand);

        Assert.Equal([row.DestinationPath], _shell.Opened);
        Assert.Equal([row.DestinationPath], _shell.Revealed);
        Assert.Equal(row.DestinationPath, _shell.Copied);
        Assert.Empty(_dialogs.Errors);
    });

    [Fact]
    public Task RemoveMovesToTrash_AndEmptyTrashDeletesHistoryOnly() => OnUiAsync(async () =>
    {
        await _harness.AddAsync(_server.Url("file/1300"), customize: r => r with { FileName = "keep.bin" });
        var row = await WaitForRowAsync(r => r.Status == DownloadStatus.Completed);
        _main.UpdateSelection([row]);

        await ExecuteAsync(_main.RemoveCommand);
        row = await WaitForRowAsync(r => r.IsDeleted);
        Assert.Equal(1, _main.Sections.Single(s => s.Kind == NavKind.Trash).Count);
        Assert.Equal(0, _main.Sections.Single(s => s.Kind == NavKind.All).Count);

        await ExecuteAsync(_main.EmptyTrashCommand);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_main.Items.Count > 0 && DateTime.UtcNow < deadline)
        {
            _main.ApplyPendingUpdates();
            await Task.Delay(50);
        }

        Assert.Empty(_main.Items);
        Assert.True(File.Exists(row.DestinationPath), "completed files stay on disk");
    });

    [Fact]
    public Task PasteUrl_OpensAddDialogWithClipboardLink() => OnUiAsync(async () =>
    {
        AddDownloadPrefill? seen = null;
        _dialogs.OnAddDownload = p =>
        {
            seen = p;
            return true;
        };
        _shell.Clipboard = "  https://example.com/tools/setup.exe  ";

        await ExecuteAsync(_main.PasteUrlCommand);
        Assert.Equal("https://example.com/tools/setup.exe", seen?.Url);

        _shell.Clipboard = "not a link";
        await ExecuteAsync(_main.PasteUrlCommand);
        Assert.Contains("does not contain", _main.StatusMessage, StringComparison.Ordinal);
    });

    [Fact]
    public Task SortingTogglesDirectionAndPersists() => OnUiAsync(async () =>
    {
        await ExecuteAsync(_main.SortCommand, "FileName");
        Assert.Equal("FileName", _main.SortColumn);
        Assert.False(_main.SortDescending);
        await ExecuteAsync(_main.SortCommand, "FileName");
        Assert.True(_main.SortDescending);
        await Task.Delay(300);
        Assert.Equal("FileName", _harness.Settings.Current.SortColumn);
    });

    [Fact]
    public Task SettingsChangesAreSavedAndApplied() => OnUiAsync(async () =>
    {
        var settings = new SettingsViewModel(_harness.Settings, _dialogs, _shell, new ThemeService(),
            new OleDownloadManager.Infrastructure.AppPaths(_harness.Root), new OleDownloadManager.Infrastructure.Media.YtDlpMediaTool(_harness.Settings, NullLogger<OleDownloadManager.Infrastructure.Media.YtDlpMediaTool>.Instance),
            NullLogger<SettingsViewModel>.Instance)
        {
            MaxActiveDownloads = 5,
            DefaultConnections = 8,
            GlobalSpeedLimit = SettingsViewModel.SpeedChoices[3],
            QueueStartText = "23:30",
            LaunchAtLogin = OleDownloadManager.Infrastructure.Windows.StartupRegistration.IsEnabled(),
        };
        settings.CategoryRows.Single(r => r.Category == DownloadCategory.Video).Extensions = "mp4 .MKV bad/ext";
        var closed = false;
        settings.CloseRequested += (_, _) => closed = true;

        await ExecuteAsync(settings.SaveCommand);

        Assert.True(closed, settings.Error);
        var saved = _harness.Settings.Current;
        Assert.Equal(5, saved.MaxActiveDownloads);
        Assert.Equal(8, saved.DefaultConnections);
        Assert.Equal(1024 * 1024, saved.GlobalSpeedLimit);
        Assert.Equal(new TimeOnly(23, 30), saved.QueueDailyStart);
        Assert.Equal([".mp4", ".mkv"], saved.CategoryExtensions[DownloadCategory.Video]);
        _main.ApplyPendingUpdates();
        Assert.Equal("Limit 1.0 MB/s", _main.SpeedLimitText);
    });

    [Fact]
    public Task InvalidSettingsAreRejected() => OnUiAsync(async () =>
    {
        var settings = new SettingsViewModel(_harness.Settings, _dialogs, _shell, new ThemeService(),
            new OleDownloadManager.Infrastructure.AppPaths(_harness.Root), new OleDownloadManager.Infrastructure.Media.YtDlpMediaTool(_harness.Settings, NullLogger<OleDownloadManager.Infrastructure.Media.YtDlpMediaTool>.Instance),
            NullLogger<SettingsViewModel>.Instance)
        {
            QueueStartText = "25:99",
            LaunchAtLogin = OleDownloadManager.Infrastructure.Windows.StartupRegistration.IsEnabled(),
        };
        await ExecuteAsync(settings.SaveCommand);
        Assert.NotNull(settings.Error);
    });

    [Fact]
    public Task RestartRestoresHistoryInANewWindowModel() => OnUiAsync(async () =>
    {
        await _harness.AddAsync(_server.Url("file/1400"), customize: r => r with { FileName = "persisted.bin" });
        await WaitForRowAsync(r => r.Status == DownloadStatus.Completed);
        _main.Dispose();

        await _harness.RestartAsync();
        _main = new MainViewModel(_harness.Coordinator, _harness.Settings, _dialogs, _shell);
        var row = Assert.Single(_main.Items);
        Assert.Equal("persisted.bin", row.FileName);
        Assert.Equal(DownloadStatus.Completed, row.Status);
    });

    [Fact]
    public Task PastingAYouTubeLink_OpensTheVideoQualityPicker_NotAFileDownload() => OnUiAsync(async () =>
    {
        var addDialogOpened = false;
        _dialogs.OnAddDownload = _ =>
        {
            addDialogOpened = true;
            return true;
        };
        _shell.Clipboard = "https://www.youtube.com/watch?v=abc123XYZ_0";

        await ExecuteAsync(_main.PasteUrlCommand);

        Assert.Equal(["https://www.youtube.com/watch?v=abc123XYZ_0"], _dialogs.MediaRequests);
        Assert.False(addDialogOpened);
        Assert.Empty(_harness.Coordinator.GetAll());
    });

    [Fact]
    public Task AddDialog_RedirectsVideoPagesInsteadOfSavingHtml() => OnUiAsync(async () =>
    {
        var dialog = NewAddDialog(new AddDownloadPrefill("https://youtu.be/abc123XYZ_0"));
        bool? closedWith = null;
        dialog.CloseRequested += (_, added) => closedWith = added;

        Assert.True(dialog.IsMediaPage);
        Assert.Contains("video page", dialog.ProbeSummary, StringComparison.OrdinalIgnoreCase);
        await ExecuteAsync(dialog.StartNowCommand);

        Assert.Equal("https://youtu.be/abc123XYZ_0", dialog.MediaRequestedUrl);
        Assert.False(closedWith);
        Assert.Empty(_harness.Coordinator.GetAll());
    });

    [Fact]
    public Task MediaDialog_OffersTheVideosQualities_AndQueuesTheChosenOne() => OnUiAsync(async () =>
    {
        await _harness.Settings.UpdateAsync(s => s.MediaNoticeAcknowledged = true);
        await _harness.Coordinator.StopQueueAsync();
        var tool = new FakeMediaTool();
        var media = new MediaViewModel(_harness.Coordinator, _harness.Settings, tool, _dialogs,
            new FileSystemService(NullLogger<FileSystemService>.Instance), "https://www.youtube.com/watch?v=abc123XYZ_0");

        // Opening with a link reads it immediately.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (media.Entries.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Single(media.Entries);
        Assert.Equal(["1080p (Full HD)  ·  ~250 MB", "720p (HD)  ·  ~100 MB", "Audio only (MP3)"], media.Qualities.Select(q => q.Display));
        Assert.Equal("1080p (Full HD)  ·  ~250 MB", media.Format.Display);

        media.Format = media.Qualities[1];
        await ExecuteAsync(media.AddToQueueCommand);

        var queued = Assert.Single(_harness.Coordinator.GetAll());
        Assert.Equal(DownloadKind.Media, queued.Kind);
        Assert.Equal(OleDownloadManager.Infrastructure.Media.MediaFormats.UpTo(720), queued.MediaFormat);
        Assert.Equal("Test video", queued.FileName);
        Assert.Equal(DownloadCategory.Video, queued.Category);
    });

    private sealed class FakeMediaTool : OleDownloadManager.Core.Interfaces.IMediaTool
    {
        public Task<OleDownloadManager.Core.Interfaces.MediaToolStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OleDownloadManager.Core.Interfaces.MediaToolStatus(true, "yt-dlp.exe", "test", null, FfmpegAvailable: true));

        public Task<OleDownloadManager.Core.Interfaces.MediaToolStatus> UpdateAsync(CancellationToken cancellationToken = default) => GetStatusAsync(cancellationToken);

        public Task<OleDownloadManager.Core.Interfaces.MediaInspection> InspectAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult(new OleDownloadManager.Core.Interfaces.MediaInspection("Test video", false,
                [new("abc", "Test video", url, TimeSpan.FromMinutes(3), 1)],
                [
                    new("1080p (Full HD)", OleDownloadManager.Infrastructure.Media.MediaFormats.UpTo(1080), 250L * 1024 * 1024, false),
                    new("720p (HD)", OleDownloadManager.Infrastructure.Media.MediaFormats.UpTo(720), 100L * 1024 * 1024, false),
                    new("Audio only (MP3)", OleDownloadManager.Infrastructure.Media.MediaFormats.AudioMp3, null, true),
                ]));

        public Task<string> DownloadAsync(Uri url, string outputDirectory, string fileStem, string format, long? rateLimitBytesPerSecond,
            IProgress<OleDownloadManager.Core.Interfaces.MediaProgress> progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
