using System.IO;
using OleDownloadManager.Core.Protocol;

namespace OleDownloadManager.App.Services;

/// <summary>Ensures one app instance per user session; later launches hand their arguments to it.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{LocalPipe.PipeName}", out var created);
        IsFirstInstance = created;
    }

    public bool IsFirstInstance { get; }

    /// <summary>Asks the running instance to show itself and optionally add a URL.</summary>
    public static async Task<bool> SignalRunningInstanceAsync(string? url)
    {
        var message = new BrowserMessage { Type = MessageTypes.Activate, Version = BrowserMessage.CurrentProtocolVersion, Url = url };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var reply = await LocalPipe.SendAsync(message, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                if (reply is not null)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
            }

            await Task.Delay(300).ConfigureAwait(false);
        }

        return false;
    }

    public void Dispose()
    {
        if (IsFirstInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
