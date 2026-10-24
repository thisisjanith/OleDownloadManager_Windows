using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OleDownloadManager.Core.Protocol;

namespace OleDownloadManager.IntegrationTests;

/// <summary>
/// Runs the real OleDownloadManager.BrowserHost.exe exactly as a browser would: origin on the command
/// line, length-prefixed JSON on stdin/stdout. A test pipe server stands in for the desktop app.
/// </summary>
public sealed class NativeHostTests : IAsyncDisposable
{
    private const string ChromeOrigin = "chrome-extension://fjlipfmbkmopdjkjnfmpocodmcecbacf/";
    private readonly string _suffix = "host-test-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _dataDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "oledm-host-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    private readonly ConcurrentQueue<BrowserMessage> _received = new();
    private LocalPipeServer? _server;

    private static string HostPath => Path.Combine(AppContext.BaseDirectory, "OleDownloadManager.BrowserHost.exe");

    private void StartFakeApp()
    {
        _server = new LocalPipeServer((message, _) =>
        {
            _received.Enqueue(message);
            var reply = BrowserMessage.Reply(message.Type == MessageTypes.AddDownload ? MessageTypes.DownloadAccepted : MessageTypes.Hello, message);
            reply.AppRunning = true;
            reply.AppVersion = "test";
            reply.DownloadId = message.Type == MessageTypes.AddDownload ? Guid.NewGuid().ToString("N") : null;
            return Task.FromResult(reply);
        }, pipeName: LocalPipe.BuildPipeName(_suffix));
        _server.Start();
    }

    private Process StartHost(params string[] args)
    {
        var start = new ProcessStartInfo(HostPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Point the host at an empty folder so it can never launch a real app instance.
            WorkingDirectory = _dataDir,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["OLEDM_PIPE_SUFFIX"] = _suffix;
        start.Environment["OLEDM_DATA_DIR"] = _dataDir;
        return Process.Start(start)!;
    }

    private static async Task SendAsync(Process host, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        var stdin = host.StandardInput.BaseStream;
        await stdin.WriteAsync(header);
        await stdin.WriteAsync(payload);
        await stdin.FlushAsync();
    }

    private static async Task<JsonElement> ReceiveAsync(Process host)
    {
        var stdout = host.StandardOutput.BaseStream;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var payload = await MessageFraming.ReadAsync(stdout, 1024 * 1024, timeout.Token);
        Assert.NotNull(payload);
        return JsonDocument.Parse(payload!).RootElement.Clone();
    }

    [Fact]
    public void HostExecutableIsBuilt() => Assert.True(File.Exists(HostPath), HostPath);

    [Fact]
    public async Task ForwardsAddDownloadToTheApp()
    {
        StartFakeApp();
        using var host = StartHost(ChromeOrigin, "--parent-window=0");
        await SendAsync(host, """{"type":"addDownload","version":1,"requestId":"r1","url":"https://example.com/files/a.zip","pageUrl":"https://example.com/","fileName":"..\\a.zip"}""");

        var reply = await ReceiveAsync(host);
        Assert.Equal("downloadAccepted", reply.GetProperty("type").GetString());
        Assert.Equal("r1", reply.GetProperty("requestId").GetString());
        Assert.True(_received.TryDequeue(out var forwarded));
        Assert.Equal("https://example.com/files/a.zip", forwarded!.Url);
        Assert.Equal("a.zip", forwarded.FileName);

        host.StandardInput.Close();
        Assert.True(host.WaitForExit(10_000));
        Assert.Equal(0, host.ExitCode);
    }

    [Fact]
    public async Task HandlesSeveralMessagesOnOneConnection()
    {
        StartFakeApp();
        using var host = StartHost(ChromeOrigin);
        await SendAsync(host, """{"type":"hello","version":1,"requestId":"a"}""");
        Assert.Equal("a", (await ReceiveAsync(host)).GetProperty("requestId").GetString());
        await SendAsync(host, """{"type":"getStatus","version":1,"requestId":"b"}""");
        Assert.Equal("b", (await ReceiveAsync(host)).GetProperty("requestId").GetString());
        host.StandardInput.Close();
        Assert.True(host.WaitForExit(10_000));
    }

    [Fact]
    public async Task ReportsAppNotRunningWithoutStartingIt()
    {
        using var host = StartHost(ChromeOrigin);
        await SendAsync(host, """{"type":"hello","version":1,"requestId":"h"}""");
        var reply = await ReceiveAsync(host);
        Assert.Equal("hello", reply.GetProperty("type").GetString());
        Assert.False(reply.GetProperty("appRunning").GetBoolean());
        host.StandardInput.Close();
        host.WaitForExit(10_000);
    }

    [Fact]
    public async Task RejectsInvalidMessagesWithoutForwarding()
    {
        StartFakeApp();
        using var host = StartHost(ChromeOrigin);
        await SendAsync(host, """{"type":"addDownload","version":1,"requestId":"x","url":"file:///C:/Windows/win.ini"}""");
        var reply = await ReceiveAsync(host);
        Assert.Equal("error", reply.GetProperty("type").GetString());

        await SendAsync(host, """{"type":"activate","version":1}""");
        Assert.Equal("error", (await ReceiveAsync(host)).GetProperty("type").GetString());
        Assert.Empty(_received);
        host.StandardInput.Close();
        host.WaitForExit(10_000);
    }

    [Theory]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [InlineData("chrome-extension://not-a-valid-id/")]
    [InlineData("")]
    public async Task RefusesUnknownCallers(string origin)
    {
        StartFakeApp();
        using var host = string.IsNullOrEmpty(origin) ? StartHost() : StartHost(origin);
        await SendAsync(host, """{"type":"hello","version":1}""");
        Assert.True(host.WaitForExit(10_000));
        Assert.Equal(2, host.ExitCode);
        Assert.Equal(0, host.StandardOutput.BaseStream.ReadByte() + 1);
    }

    [Fact]
    public async Task AcceptsFirefoxCallerArguments()
    {
        StartFakeApp();
        using var host = StartHost(@"C:\Some\com.oledm.downloadmanager.json", "oledownloadmanager@oledm.local");
        await SendAsync(host, """{"type":"hello","version":1,"requestId":"f"}""");
        Assert.Equal("f", (await ReceiveAsync(host)).GetProperty("requestId").GetString());
        host.StandardInput.Close();
        host.WaitForExit(10_000);
    }

    [Fact]
    public async Task ExitsOnOversizedFrame()
    {
        using var host = StartHost(ChromeOrigin);
        var stdin = host.StandardInput.BaseStream;
        await stdin.WriteAsync(new byte[] { 0x00, 0x00, 0x00, 0x10 });
        await stdin.FlushAsync();
        Assert.True(host.WaitForExit(10_000));
        Assert.Equal(1, host.ExitCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }
}
