using System.Reflection;
using Microsoft.Extensions.Logging;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Protocol;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.App.Services;

/// <summary>
/// Handles validated messages arriving on the local pipe from the native messaging host
/// (browser extension) or from a second app instance.
/// </summary>
public sealed class BrowserRequestHandler
{
    private readonly IDownloadCoordinator _coordinator;
    private readonly ISettingsStore _settings;
    private readonly ILogger<BrowserRequestHandler> _logger;
    private readonly HashSet<string> _pendingDialogs = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public BrowserRequestHandler(IDownloadCoordinator coordinator, ISettingsStore settings, ILogger<BrowserRequestHandler> logger)
    {
        _coordinator = coordinator;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Shows the main window (on the UI thread).</summary>
    public Action? ActivateWindow { get; set; }

    /// <summary>Opens the Add Download dialog with values from the browser; returns when the dialog closes.</summary>
    public Func<AddDownloadPrefill, Task>? ShowAddDialog { get; set; }

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";

    public async Task<BrowserMessage> HandleAsync(BrowserMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case MessageTypes.Hello:
            {
                var reply = BrowserMessage.Reply(MessageTypes.Hello, message);
                reply.AppVersion = AppVersion;
                reply.AppRunning = true;
                return reply;
            }

            case MessageTypes.GetStatus:
            {
                var reply = BrowserMessage.Reply(MessageTypes.Status, message);
                reply.AppVersion = AppVersion;
                reply.AppRunning = true;
                reply.ActiveDownloads = _coordinator.ActiveCount;
                return reply;
            }

            case MessageTypes.Activate:
                ActivateWindow?.Invoke();
                if (message.Url is { } activateUrl && UrlValidator.IsValid(activateUrl))
                {
                    _ = ShowDialogAsync(new AddDownloadPrefill(activateUrl));
                }

                return BrowserMessage.Reply(MessageTypes.Status, message);

            case MessageTypes.AddDownload:
                return await AddAsync(message, cancellationToken).ConfigureAwait(false);

            default:
                return BrowserMessage.ErrorReply("Unsupported message type.", message);
        }
    }

    private async Task<BrowserMessage> AddAsync(BrowserMessage message, CancellationToken cancellationToken)
    {
        var uri = new Uri(message.Url!);
        _logger.LogInformation("Browser requested download of {Url}", LogRedactor.Url(uri));

        if (_coordinator.FindActiveDuplicate(uri) is { } duplicate)
        {
            return Rejected(message, $"Already in the download list ({duplicate.Status.ToDisplayString()}).");
        }

        // Video pages always go through the dialog, which opens the media quality picker.
        if (_settings.Current.ShowDialogForBrowserDownloads || MediaUrlDetector.IsMediaPage(uri))
        {
            lock (_lock)
            {
                if (!_pendingDialogs.Add(uri.AbsoluteUri))
                {
                    return Rejected(message, "This link is already waiting for confirmation.");
                }
            }

            _ = ShowDialogAsync(new AddDownloadPrefill(uri.AbsoluteUri, message.FileName, message.PageUrl, DownloadSource.Browser));
            var accepted = BrowserMessage.Reply(MessageTypes.DownloadAccepted, message);
            accepted.Reason = "Waiting for confirmation in OleDownloadManager.";
            return accepted;
        }

        var result = await _coordinator.AddAsync(new DownloadRequest
        {
            Url = uri,
            FileName = message.FileName,
            PageUrl = Uri.TryCreate(message.PageUrl, UriKind.Absolute, out var page) ? page : null,
            Source = DownloadSource.Browser,
            StartNow = true,
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Accepted)
        {
            return Rejected(message, result.Error ?? "The download was rejected.");
        }

        var reply = BrowserMessage.Reply(MessageTypes.DownloadAccepted, message);
        reply.DownloadId = result.DownloadId!.Value.ToString("N");
        return reply;
    }

    private async Task ShowDialogAsync(AddDownloadPrefill prefill)
    {
        try
        {
            ActivateWindow?.Invoke();
            if (ShowAddDialog is { } show)
            {
                await show(prefill).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not show the Add Download dialog for a browser request");
        }
        finally
        {
            if (prefill.Url is { } url)
            {
                lock (_lock)
                {
                    _pendingDialogs.Remove(url);
                }
            }
        }
    }

    private static BrowserMessage Rejected(BrowserMessage request, string reason)
    {
        var reply = BrowserMessage.Reply(MessageTypes.DownloadRejected, request);
        reply.Reason = reason;
        return reply;
    }
}
