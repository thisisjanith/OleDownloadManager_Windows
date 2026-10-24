using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using OleDownloadManager.App.ViewModels;
using OleDownloadManager.App.Views;
using OleDownloadManager.Core.Interfaces;

namespace OleDownloadManager.App.Services;

public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _services;
    private readonly ThemeService _theme;

    public DialogService(IServiceProvider services, ThemeService theme)
    {
        _services = services;
        _theme = theme;
    }

    private static Window? Owner
    {
        get
        {
            var active = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            var main = Application.Current.MainWindow;
            var candidate = active ?? main;
            return candidate is { IsVisible: true } ? candidate : null;
        }
    }

    public bool ShowAddDownload(AddDownloadPrefill? prefill)
    {
        var viewModel = new AddDownloadViewModel(
            _services.GetRequiredService<IDownloadCoordinator>(),
            _services.GetRequiredService<ISettingsStore>(),
            _services.GetRequiredService<IFileSystemService>(),
            this,
            prefill);
        var window = new AddDownloadWindow(viewModel);
        var added = ShowModal(window) == true;
        if (viewModel.MediaRequestedUrl is { } mediaUrl)
        {
            // The address was a video page; continue in the media dialog to pick a quality.
            ShowMediaDialog(mediaUrl);
        }

        return added;
    }

    public void ShowSettings()
    {
        var window = new SettingsWindow(ActivatorUtilities.CreateInstance<SettingsViewModel>(_services, this));
        ShowModal(window);
    }

    public void ShowMediaDialog(string? url)
    {
        var viewModel = new MediaViewModel(
            _services.GetRequiredService<IDownloadCoordinator>(),
            _services.GetRequiredService<ISettingsStore>(),
            _services.GetRequiredService<IMediaTool>(),
            this,
            _services.GetRequiredService<IFileSystemService>(),
            url);
        ShowModal(new MediaWindow(viewModel));
    }

    public bool Confirm(string title, string message, string confirmText = "OK")
    {
        var owner = Owner;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
            : MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
    }

    public void ShowError(string title, string message)
    {
        var owner = Owner;
        if (owner is null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public ScheduleChoice AskSchedule(DateTimeOffset? current)
    {
        var window = new ScheduleWindow(new ScheduleViewModel(current));
        if (ShowModal(window) != true)
        {
            return new ScheduleChoice(false, null);
        }

        return new ScheduleChoice(true, window.Cleared ? null : window.Result);
    }

    public string? PickFolder(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        var owner = Owner;
        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FolderName : null;
    }

    public void ShowAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            version = version[..plus];
        }

        var text = $"OleDownloadManager {version}\n\nA download manager for Windows with segmented downloads, pause and resume, queues and browser integration.\n\n.NET {Environment.Version} · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}";
        if (Owner is { } owner)
        {
            MessageBox.Show(owner, text, "About OleDownloadManager", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(text, "About OleDownloadManager", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool? ShowModal(Window window)
    {
        var owner = Owner;
        if (owner is not null)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowInTaskbar = true;
        }

        _theme.ApplyTitleBar(window);
        return window.ShowDialog();
    }
}
