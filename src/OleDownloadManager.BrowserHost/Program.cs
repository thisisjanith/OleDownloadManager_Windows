using System.Diagnostics;
using System.Text.Json;
using OleDownloadManager.Core.Protocol;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.BrowserHost;

/// <summary>
/// Native messaging host launched by Chrome, Edge or Firefox. It speaks the browser's length-prefixed
/// JSON protocol on stdin/stdout (stdout carries protocol messages only), validates every message and
/// forwards allowed requests to the running app over a per-user named pipe. Diagnostics go to a log file.
/// </summary>
internal static class Program
{
    /// <summary>Messages from the browser are capped well below Chrome's 64 MiB limit.</summary>
    private const int MaxInboundBytes = BrowserMessageValidator.MaxMessageBytes;
    private const string AppExecutableName = "OleDownloadManager.exe";

    private static async Task<int> Main(string[] args)
    {
        var log = new HostLog();
        var origin = CallerOrigin.FromArguments(args);
        var allowed = AllowedOrigins.Load(AppContext.BaseDirectory);
        if (origin is null || !allowed.IsAllowed(origin))
        {
            log.Write($"Rejected caller origin: {origin?.ToString() ?? "(none)"}");
            return 2;
        }

        log.Write($"Started for {origin}");
        await using var stdin = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        try
        {
            await RunAsync(stdin, stdout, log, AppContext.BaseDirectory, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            log.Write("Protocol error: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Processes messages until the browser closes stdin.</summary>
    internal static async Task RunAsync(Stream input, Stream output, HostLog log, string baseDirectory, CancellationToken cancellationToken)
    {
        while (true)
        {
            var payload = await MessageFraming.ReadAsync(input, MaxInboundBytes, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                log.Write("Browser closed the connection");
                return;
            }

            BrowserMessage reply;
            if (!BrowserMessageValidator.TryParse(payload, MessageTypes.FromExtension, out var message, out var error))
            {
                log.Write("Rejected message: " + error);
                reply = BrowserMessage.ErrorReply(error ?? "Invalid message.", message);
            }
            else
            {
                reply = await ForwardAsync(message!, log, baseDirectory, cancellationToken).ConfigureAwait(false);
            }

            await MessageFraming.WriteAsync(output, reply, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<BrowserMessage> ForwardAsync(BrowserMessage message, HostLog log, string baseDirectory, CancellationToken cancellationToken)
    {
        if (message.Type == MessageTypes.AddDownload)
        {
            log.Write("addDownload " + LogRedactor.Url(message.Url));
        }

        var reply = await TrySendAsync(message, TimeSpan.FromSeconds(2), log, cancellationToken).ConfigureAwait(false);
        if (reply is not null)
        {
            return reply;
        }

        // Status queries must not start the app; downloads may.
        if (message.Type != MessageTypes.AddDownload)
        {
            var status = BrowserMessage.Reply(message.Type == MessageTypes.Hello ? MessageTypes.Hello : MessageTypes.Status, message);
            status.AppRunning = false;
            return status;
        }

        if (!TryStartApp(baseDirectory, log))
        {
            return BrowserMessage.ErrorReply("OleDownloadManager is not running and could not be started.", message);
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            reply = await TrySendAsync(message, TimeSpan.FromSeconds(1), log, cancellationToken).ConfigureAwait(false);
            if (reply is not null)
            {
                return reply;
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return BrowserMessage.ErrorReply("OleDownloadManager did not respond. Try again.", message);
    }

    private static async Task<BrowserMessage?> TrySendAsync(BrowserMessage message, TimeSpan connectTimeout, HostLog log, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await LocalPipe.SendAsync(message, connectTimeout, cancellationToken).ConfigureAwait(false);
            if (reply is not null)
            {
                // Never forward unexpected fields or types from the pipe back to the browser.
                reply.Version = BrowserMessage.CurrentProtocolVersion;
                reply.RequestId = message.RequestId;
            }

            return reply;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or EndOfStreamException or OperationCanceledException)
        {
            log.Write("Pipe error: " + ex.GetType().Name);
            return null;
        }
    }

    private static bool TryStartApp(string baseDirectory, HostLog log)
    {
        var path = Path.Combine(baseDirectory, AppExecutableName);
        if (!File.Exists(path))
        {
            log.Write("App executable not found next to the host");
            return false;
        }

        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = baseDirectory };
            start.ArgumentList.Add("--background");
            Process.Start(start)?.Dispose();
            log.Write("Started the app");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Write("Could not start the app: " + ex.Message);
            return false;
        }
    }
}

/// <summary>The extension that launched the host, as reported by the browser on the command line.</summary>
internal sealed record CallerOrigin(string Browser, string ExtensionId)
{
    /// <summary>
    /// Chrome/Edge pass "chrome-extension://&lt;id&gt;/" as the first argument (plus --parent-window on Windows).
    /// Firefox passes the manifest path followed by the add-on ID.
    /// </summary>
    public static CallerOrigin? FromArguments(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            const string prefix = "chrome-extension://";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                var id = arg[prefix.Length..].TrimEnd('/');
                return id.Length == 32 && id.All(c => c is >= 'a' and <= 'p') ? new CallerOrigin("chromium", id) : null;
            }
        }

        if (args.Count >= 2 && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && args[1].Length is > 0 and <= 200 &&
            !args[1].Contains('/', StringComparison.Ordinal) && !args[1].Contains('\\', StringComparison.Ordinal))
        {
            return new CallerOrigin("firefox", args[1]);
        }

        return null;
    }

    public override string ToString() => $"{Browser}:{ExtensionId}";
}

/// <summary>
/// Extension IDs this host serves. Defaults to the published OleDownloadManager extensions; installs can
/// add IDs (e.g. a store-assigned Chrome ID) in browserhost.allowed.json next to the executable.
/// </summary>
internal sealed class AllowedOrigins
{
    public const string FileName = "browserhost.allowed.json";
    public const string DefaultChromeId = "fjlipfmbkmopdjkjnfmpocodmcecbacf";
    public const string DefaultFirefoxId = "oledownloadmanager@oledm.local";

    private readonly HashSet<string> _chromium = new(StringComparer.Ordinal) { DefaultChromeId };
    private readonly HashSet<string> _firefox = new(StringComparer.Ordinal) { DefaultFirefoxId };

    public static AllowedOrigins Load(string directory)
    {
        var result = new AllowedOrigins();
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var (property, set) in new[] { ("chromium", result._chromium), ("firefox", result._firefox) })
            {
                if (document.RootElement.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.GetString() is { Length: > 0 } id && !id.Contains('*', StringComparison.Ordinal))
                        {
                            set.Add(id);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fall back to the built-in IDs.
        }

        return result;
    }

    public bool IsAllowed(CallerOrigin origin) => origin.Browser switch
    {
        "chromium" => _chromium.Contains(origin.ExtensionId),
        "firefox" => _firefox.Contains(origin.ExtensionId),
        _ => false,
    };
}

/// <summary>Append-only diagnostic log; never written to stdout, which carries protocol messages.</summary>
internal sealed class HostLog
{
    private readonly string? _path;

    public HostLog()
    {
        try
        {
            var dataDir = Environment.GetEnvironmentVariable("OLEDM_DATA_DIR") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OleDownloadManager");
            var logs = Path.Combine(dataDir, "logs");
            Directory.CreateDirectory(logs);
            _path = Path.Combine(logs, $"browserhost-{DateTime.Now:yyyyMMdd}.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _path = null;
        }
    }

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}";
        try
        {
            if (_path is not null)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(line);
        }
    }
}
