using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace OleDownloadManager.Core.Protocol;

/// <summary>
/// Local IPC between the native messaging host (or a second app instance) and the running app.
/// The pipe is created with <see cref="PipeOptions.CurrentUserOnly"/>, so only processes of the
/// same Windows user can connect, and clients verify the server belongs to that user as well.
/// One request and one reply per connection, using native-messaging framing.
/// </summary>
public static class LocalPipe
{
    public static string PipeName { get; } = BuildPipeName(Environment.GetEnvironmentVariable("OLEDM_PIPE_SUFFIX"));

    public static string BuildPipeName(string? suffix)
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}|{suffix}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $"OleDownloadManager-{hash}";
    }

    /// <summary>Sends a message to the running app and returns its reply, or null if the app is not running.</summary>
    public static async Task<BrowserMessage?> SendAsync(BrowserMessage message, TimeSpan connectTimeout, CancellationToken cancellationToken, string? pipeName = null)
    {
        await using var client = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(connectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }

        await MessageFraming.WriteAsync(client, message, cancellationToken).ConfigureAwait(false);
        using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        replyTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        var reply = await MessageFraming.ReadAsync(client, BrowserMessageValidator.MaxMessageBytes, replyTimeout.Token).ConfigureAwait(false);
        if (reply is null)
        {
            return null;
        }

        return System.Text.Json.JsonSerializer.Deserialize(reply, BrowserMessageJsonContext.Default.BrowserMessage);
    }
}

/// <summary>Accepts local pipe connections and dispatches validated messages to a handler.</summary>
public sealed class LocalPipeServer : IAsyncDisposable
{
    private readonly Func<BrowserMessage, CancellationToken, Task<BrowserMessage>> _handler;
    private readonly Action<Exception>? _onError;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;

    public LocalPipeServer(Func<BrowserMessage, CancellationToken, Task<BrowserMessage>> handler, Action<Exception>? onError = null, string? pipeName = null)
    {
        _handler = handler;
        _onError = onError;
        _pipeName = pipeName ?? LocalPipe.PipeName;
    }

    public void Start() => _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                _onError?.Invoke(ex);
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            _ = HandleConnectionAsync(server, ct);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        await using (server)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var payload = await MessageFraming.ReadAsync(server, BrowserMessageValidator.MaxMessageBytes, timeout.Token).ConfigureAwait(false);
                if (payload is null)
                {
                    return;
                }

                BrowserMessage reply;
                if (!BrowserMessageValidator.TryParse(payload, MessageTypes.FromLocalClient, out var message, out var error))
                {
                    reply = BrowserMessage.ErrorReply(error ?? "Invalid message.", message);
                }
                else
                {
                    reply = await _handler(message!, timeout.Token).ConfigureAwait(false);
                }

                await MessageFraming.WriteAsync(server, reply, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or EndOfStreamException)
            {
                // Client went away or sent garbage; nothing to answer.
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
