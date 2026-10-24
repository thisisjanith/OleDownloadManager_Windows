using System.Windows;
using Microsoft.Extensions.Logging;
using OleDownloadManager.Core.Interfaces;
using Forms = System.Windows.Forms;

namespace OleDownloadManager.App.Services;

/// <summary>
/// Notification-area icon with Open / Pause All / Resume All / Exit, and the channel for
/// Windows notifications (balloon tips appear as toasts on Windows 10 and 11). Notifications
/// therefore require the icon to be enabled; every notification is also echoed in the status bar.
/// </summary>
public sealed class TrayIconService : INotificationService, IDisposable
{
    private readonly ILogger<TrayIconService> _logger;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _activeItem;
    private DateTime _lastBalloon = DateTime.MinValue;
    private bool _disposed;

    public TrayIconService(ILogger<TrayIconService> logger)
    {
        _logger = logger;
        _icon = new Forms.NotifyIcon
        {
            Text = "OleDownloadManager",
            Icon = LoadIcon(),
            Visible = false,
        };

        _activeItem = new Forms.ToolStripMenuItem("No active downloads") { Enabled = false };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Open OleDownloadManager", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty)) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_activeItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Pause All", null, (_, _) => PauseAllRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Resume All", null, (_, _) => ResumeAllRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? PauseAllRequested;

    public event EventHandler? ResumeAllRequested;

    public event EventHandler? ExitRequested;

    /// <summary>Raised for every notification so the main window can show it in the status bar.</summary>
    public event EventHandler<string>? NotificationRaised;

    public bool NotificationsEnabled { get; set; } = true;

    public bool IsVisible
    {
        get => !_disposed && _icon.Visible;
        set
        {
            if (!_disposed)
            {
                _icon.Visible = value;
            }
        }
    }

    public void UpdateStatus(int activeDownloads, string speedText)
    {
        if (_disposed)
        {
            return;
        }

        var text = activeDownloads == 0 ? "No active downloads" : $"{activeDownloads} active · {speedText}";
        _activeItem.Text = text;
        var tooltip = activeDownloads == 0 ? "OleDownloadManager" : $"OleDownloadManager — {text}";
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    public void Notify(string title, string message, NotificationKind kind)
    {
        _logger.LogInformation("Notification: {Title}", title);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            NotificationRaised?.Invoke(this, $"{title}: {message}");
            if (_disposed || !NotificationsEnabled || !_icon.Visible)
            {
                return;
            }

            // Avoid a burst of toasts when many downloads finish together.
            if (DateTime.UtcNow - _lastBalloon < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _lastBalloon = DateTime.UtcNow;
            var icon = kind switch
            {
                NotificationKind.Error => Forms.ToolTipIcon.Error,
                NotificationKind.Warning => Forms.ToolTipIcon.Warning,
                _ => Forms.ToolTipIcon.Info,
            };
            _icon.ShowBalloonTip(5000, title, message.Length > 250 ? message[..250] : message, icon);
        });
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/OleDownloadManager;component/Assets/app.ico"));
        if (resource is null)
        {
            return System.Drawing.SystemIcons.Application;
        }

        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
