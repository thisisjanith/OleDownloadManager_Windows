using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OleDownloadManager.App.Mvvm;
using OleDownloadManager.App.Services;
using OleDownloadManager.App.ViewModels;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Protocol;
using OleDownloadManager.Core.Validation;
using OleDownloadManager.Engine;
using OleDownloadManager.Infrastructure;
using OleDownloadManager.Infrastructure.FileSystem;
using OleDownloadManager.Infrastructure.Logging;
using OleDownloadManager.Infrastructure.Media;
using OleDownloadManager.Infrastructure.Persistence;
using OleDownloadManager.Infrastructure.Security;
using OleDownloadManager.Infrastructure.Settings;

namespace OleDownloadManager.App;

/// <summary>
/// Composition root and application lifetime: single instance, dependency injection, startup
/// recovery, tray icon, local IPC for the browser host, and graceful shutdown.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private ServiceProvider? _services;
    private LocalPipeServer? _pipeServer;
    private MainWindow? _window;
    private TrayIconService? _tray;
    private ClipboardMonitor? _clipboard;
    private ILogger<App>? _logger;
    private bool _exiting;
    private bool _trayHintShown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var background = e.Args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
        var url = e.Args.FirstOrDefault(a => a.Contains("://", StringComparison.Ordinal) && UrlValidator.IsValid(a));

        _instance = new SingleInstance();
        if (!_instance.IsFirstInstance)
        {
            await SingleInstance.SignalRunningInstanceAsync(url);
            Shutdown();
            return;
        }

        try
        {
            await StartAsync(background, url);
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Startup failed");
            MessageBox.Show("OleDownloadManager could not start:\n\n" + ex.Message, "OleDownloadManager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync(bool background, string? url)
    {
        var paths = new AppPaths();
        paths.EnsureCreated();
        _services = ConfigureServices(paths);
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Starting OleDownloadManager {Version}", BrowserRequestHandler.AppVersion);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        AsyncRelayCommand.UnhandledErrorHandler = ex =>
        {
            _logger.LogError(ex, "Command failed");
            _services.GetRequiredService<IDialogService>().ShowError("OleDownloadManager", ex.Message);
        };

        var settings = _services.GetRequiredService<ISettingsStore>();
        await settings.LoadAsync();
        var theme = _services.GetRequiredService<ThemeService>();
        theme.Apply(settings.Current.Theme);

        var coordinator = _services.GetRequiredService<IDownloadCoordinator>();
        await coordinator.InitializeAsync();

        _tray = _services.GetRequiredService<TrayIconService>();
        _tray.IsVisible = settings.Current.ShowTrayIcon;
        _tray.NotificationsEnabled = settings.Current.NotificationsEnabled;
        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.PauseAllRequested += async (_, _) => await coordinator.PauseAllAsync();
        _tray.ResumeAllRequested += async (_, _) => await coordinator.ResumeAllAsync();
        _tray.ExitRequested += async (_, _) => await ExitAsync(confirm: false);

        var viewModel = _services.GetRequiredService<MainViewModel>();
        _tray.NotificationRaised += (_, text) => viewModel.StatusMessage = text;
        viewModel.StatusUpdated += (_, _) => _tray.UpdateStatus(viewModel.ActiveCount, viewModel.TotalSpeedText);

        _window = new MainWindow(viewModel);
        MainWindow = _window;
        _window.RestorePlacement(settings.Current.MainWindowPlacement);
        theme.ApplyTitleBar(_window);
        _window.Closing += OnMainWindowClosing;
        _window.ExitRequested += async (_, _) => await ExitAsync(confirm: true);

        // Never start hidden unless there is a visible way back (the tray icon).
        if (!background || !_tray.IsVisible)
        {
            _window.Show();
            if (background)
            {
                _window.WindowState = WindowState.Minimized;
            }
        }

        var handler = _services.GetRequiredService<BrowserRequestHandler>();
        handler.ActivateWindow = () => Dispatcher.BeginInvoke(ShowMainWindow);
        handler.ShowAddDialog = prefill => Dispatcher.InvokeAsync(() => viewModel.AddDownload(prefill)).Task;
        _pipeServer = new LocalPipeServer(handler.HandleAsync, ex => _logger.LogWarning(ex, "Local pipe error"));
        _pipeServer.Start();

        _clipboard = _services.GetRequiredService<ClipboardMonitor>();
        _clipboard.LinkDetected += (_, link) => Dispatcher.BeginInvoke(() =>
        {
            ShowMainWindow();
            viewModel.AddDownload(new AddDownloadPrefill(link, Source: Core.Enums.DownloadSource.Clipboard));
        });
        ApplyClipboardSetting(settings.Current);

        settings.SettingsChanged += (_, updated) => Dispatcher.BeginInvoke(() =>
        {
            // Saving the window position during exit also raises this; the tray may already be gone.
            if (_exiting)
            {
                return;
            }

            _tray.IsVisible = updated.ShowTrayIcon;
            _tray.NotificationsEnabled = updated.NotificationsEnabled;
            ApplyClipboardSetting(updated);
        });

        if (url is not null)
        {
            ShowMainWindow();
            viewModel.AddDownload(new AddDownloadPrefill(url));
        }
    }

    private static ServiceProvider ConfigureServices(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(paths.LogDirectory));
        });

        services.AddSingleton(sp => new SqliteDatabase(paths.DatabasePath, sp.GetRequiredService<ILogger<SqliteDatabase>>()));
        services.AddSingleton<IDownloadRepository, SqliteDownloadRepository>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IMediaTool, YtDlpMediaTool>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<TrayIconService>());
        services.AddSingleton(_ => HttpRequestSender.CreateClient(HttpRequestSender.CreateHandler(TimeSpan.FromSeconds(30))));
        services.AddSingleton(sp => new EngineOptions
        {
            ReadInactivityTimeout = TimeSpan.FromSeconds(sp.GetRequiredService<ISettingsStore>().Current.ReadTimeoutSeconds),
            UserAgent = $"OleDownloadManager/{BrowserRequestHandler.AppVersion} (Windows)",
        });
        services.AddSingleton<IDownloadCoordinator>(sp => new DownloadCoordinator(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IDownloadRepository>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IFileSystemService>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<INotificationService>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<EngineOptions>(),
            TimeProvider.System,
            sp.GetRequiredService<IMediaTool>()));

        services.AddSingleton<ThemeService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<BrowserRequestHandler>();
        services.AddSingleton<ClipboardMonitor>();
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }

    private void ApplyClipboardSetting(AppSettings settings)
    {
        if (_clipboard is null || _window is null)
        {
            return;
        }

        if (settings.ClipboardMonitoring)
        {
            _clipboard.Attach(_window);
        }
        else
        {
            _clipboard.Detach();
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        var settings = _services!.GetRequiredService<ISettingsStore>().Current;
        if (settings.CloseToTray && _tray?.IsVisible == true)
        {
            SavePlacement();
            _window!.Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.Notify("OleDownloadManager is still running", "Downloads continue in the background. Use the notification-area icon to reopen or exit.", NotificationKind.Info);
            }

            return;
        }

        _ = ExitAsync(confirm: true);
    }

    private void SavePlacement()
    {
        if (_window is null || _services is null)
        {
            return;
        }

        var placement = _window.CapturePlacement();
        _ = _services.GetRequiredService<ISettingsStore>().UpdateAsync(s => s.MainWindowPlacement = placement);
    }

    /// <summary>Stops transfers (persisting resumable state) and exits.</summary>
    private async Task ExitAsync(bool confirm)
    {
        if (_exiting || _services is null)
        {
            return;
        }

        var coordinator = _services.GetRequiredService<IDownloadCoordinator>();
        // Count by status: a job that just completed may not have released its slot yet.
        var active = coordinator.GetAll().Count(s => s.Status.IsActive() && !s.IsDeleted);
        if (confirm && active > 0 && !_services.GetRequiredService<IDialogService>().Confirm("Exit OleDownloadManager",
                $"{active} download(s) are in progress. Resumable downloads will continue the next time you start the app.\n\nExit now?", "Exit"))
        {
            return;
        }

        _exiting = true;
        try
        {
            if (_window is not null)
            {
                var placement = _window.CapturePlacement();
                await _services.GetRequiredService<ISettingsStore>().UpdateAsync(s => s.MainWindowPlacement = placement);
                _window.Hide();
            }

            await coordinator.ShutdownAsync();
            if (_pipeServer is not null)
            {
                await _pipeServer.DisposeAsync();
            }

            _clipboard?.Dispose();
            _tray?.Dispose();
            _services.GetService<MainViewModel>()?.Dispose();
            _logger?.LogInformation("Exiting");

            // The container owns async-disposable services (the coordinator), so dispose asynchronously.
            var services = _services;
            _services = null;
            await services.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during shutdown");
        }
        finally
        {
            _window?.Close();
            Shutdown();
        }
    }

    /// <summary>
    /// Windows sign-out, shutdown, or an installer's Restart Manager closing the app: stop transfers so
    /// resumable progress is flushed and recorded before the process ends.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (_exiting || _services is null)
        {
            return;
        }

        _exiting = true;
        _logger?.LogInformation("Session ending ({Reason}); stopping downloads", e.ReasonSessionEnding);
        try
        {
            // The engine never needs the UI thread to stop, so a bounded synchronous wait is safe here.
            _services.GetRequiredService<IDownloadCoordinator>().ShutdownAsync().Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error while stopping downloads at session end");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        if (_exiting)
        {
            // Never block shutdown with a dialog; the error is in the log.
            return;
        }

        MessageBox.Show("An unexpected error occurred:\n\n" + e.Exception.Message + "\n\nDetails were written to the log.", "OleDownloadManager", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
