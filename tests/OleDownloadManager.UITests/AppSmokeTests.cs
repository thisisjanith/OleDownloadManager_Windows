using System.Diagnostics;
using System.Windows.Automation;
using OleDownloadManager.IntegrationTests.Infrastructure;

namespace OleDownloadManager.UITests;

/// <summary>
/// Launches the real OleDownloadManager.exe in an isolated data folder and drives it through Windows
/// UI Automation: add a download from the command line, confirm the dialog, verify the list, search,
/// close, relaunch and check that history was restored.
/// </summary>
[Collection("RealApp")]
public sealed class AppSmokeTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "oledm-ui-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    private readonly string _pipeSuffix = "ui-" + Guid.NewGuid().ToString("N")[..8];
    private TestHttpServer _server = null!;
    private Process? _app;

    /// <summary>
    /// The app's own build output (with its own deps/runtimeconfig), or OLEDM_APP_PATH to test a published build.
    /// </summary>
    private static string AppPath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("OLEDM_APP_PATH") is { Length: > 0 } custom)
            {
                return custom;
            }

            var configuration = AppContext.BaseDirectory.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OleDownloadManager.sln")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory?.FullName ?? ".", "src", "OleDownloadManager.App", "bin", configuration, "net10.0-windows", "OleDownloadManager.exe");
        }
    }

    public async ValueTask InitializeAsync() => _server = await TestHttpServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_app is { HasExited: false })
        {
            _app.Kill(entireProcessTree: true);
            await _app.WaitForExitAsync();
        }

        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private Process Launch(params string[] args)
    {
        var start = new ProcessStartInfo(AppPath) { UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["OLEDM_DATA_DIR"] = Path.Combine(_root, "data");
        start.Environment["OLEDM_PIPE_SUFFIX"] = _pipeSuffix;
        return Process.Start(start)!;
    }

    private static AutomationElement WaitFor(Process process, string automationId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var byProcess = new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id);
        var byId = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        while (DateTime.UtcNow < deadline)
        {
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, byProcess))
            {
                if (window.Current.AutomationId == automationId)
                {
                    return window;
                }

                if (window.FindFirst(TreeScope.Descendants, byId) is { } found)
                {
                    return found;
                }
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException($"UI element '{automationId}' did not appear.");
    }

    /// <summary>Rows of the download list (a GridView exposes them as DataItem).</summary>
    private static AutomationElement[] ListItems(AutomationElement list) =>
        list.FindAll(TreeScope.Children, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))).Cast<AutomationElement>().ToArray();

    private static void WaitUntil(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(what);
            }

            Thread.Sleep(200);
        }
    }

    /// <summary>Open windows and the end of the app log, for failure messages.</summary>
    private string Diagnostics(Process process)
    {
        var windows = process.HasExited
            ? []
            : AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id))
                .Cast<AutomationElement>().Select(w => $"{w.Current.AutomationId}:{w.Current.Name}").ToArray();
        var logs = Directory.Exists(Path.Combine(_root, "data", "logs"))
            ? Directory.GetFiles(Path.Combine(_root, "data", "logs"), "oledm-*.log").SelectMany(f =>
            {
                using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n').TakeLast(8);
            })
            : [];
        return $"Windows: [{string.Join(", ", windows)}] Log: {string.Join(" | ", logs)}";
    }

    [Fact]
    public void AddDownloadFromCommandLine_ShowsInList_AndSurvivesRestart()
    {
        Assert.True(File.Exists(AppPath), AppPath);
        var downloads = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;

        _app = Launch(_server.Url("disposition/180000?name=smoke-test.zip").AbsoluteUri);
        WaitFor(_app, "AddDownloadWindow");
        ((ValuePattern)WaitFor(_app, "DestinationBox").GetCurrentPattern(ValuePattern.Pattern)).SetValue(downloads);
        Thread.Sleep(1500);
        ((InvokePattern)WaitFor(_app, "StartNowButton").GetCurrentPattern(InvokePattern.Pattern)).Invoke();

        var target = Path.Combine(downloads, "smoke-test.zip");
        WaitUntil(() => File.Exists(target), "the downloaded file to appear");
        Assert.Equal(TestContent.Sha256(180000), TestContent.FileSha256(target));

        var list = WaitFor(_app, "DownloadList");
        WaitUntil(() => ListItems(list).Any(i => i.Current.Name == "smoke-test.zip"), "the row to appear in the list");

        // Searching filters the real list.
        var search = (ValuePattern)WaitFor(_app, "SearchBox").GetCurrentPattern(ValuePattern.Pattern);
        search.SetValue("no-such-file");
        WaitUntil(() => ListItems(list).Length == 0, "search to hide the row");
        search.SetValue("smoke");
        WaitUntil(() => ListItems(list).Length == 1, "search to show the row");

        // Close the window (no active downloads, so no confirmation) and relaunch.
        var main = WaitFor(_app, "MainWindow");
        ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Assert.True(_app.WaitForExit(20_000), "the app should exit after the window is closed. " + Diagnostics(_app));

        _app = Launch();
        list = WaitFor(_app, "DownloadList");
        WaitUntil(() => ListItems(list).Any(i => i.Current.Name == "smoke-test.zip"), "history to be restored after restart");
    }

    [Fact]
    public void SecondLaunchHandsItsUrlToTheRunningInstance()
    {
        _app = Launch();
        WaitFor(_app, "DownloadList");

        using var second = Launch(_server.Url("file/1000").AbsoluteUri);
        Assert.True(second.WaitForExit(20_000), "the second instance should exit after signalling the first");
        WaitFor(_app, "AddDownloadWindow");
        Assert.False(_app.HasExited);
    }
}
