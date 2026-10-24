using System.Diagnostics;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Models;
using OleDownloadManager.IntegrationTests.Infrastructure;

namespace OleDownloadManager.IntegrationTests;

/// <summary>Pause/resume, application restart recovery, queue limits and bandwidth throttling.</summary>
public sealed class ResumeAndQueueTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private EngineHarness _harness = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        _harness = await EngineHarness.CreateAsync(s => s.AutoResumeOnStartup = false);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task PauseAndResume_ContinuesFromPartialData_WithoutDuplicatingBytes()
    {
        const long size = 3 * 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"file/{size}?bps=500000"));
        await _harness.WaitForAsync(id, s => s.DownloadedBytes > 400_000);

        await _harness.Coordinator.PauseAsync([id]);
        var paused = _harness.Coordinator.Get(id)!;
        Assert.Equal(DownloadStatus.Paused, paused.Status);
        Assert.True(paused.HasPartialData);

        // The persisted record matches what is on disk.
        var stored = (await _harness.Repository.LoadAllAsync()).Single(i => i.Id == id);
        Assert.Equal(DownloadStatus.Paused, stored.Status);
        Assert.True(stored.Segments.Sum(s => s.DownloadedBytes) > 0);
        foreach (var segment in stored.Segments.Where(s => s.DownloadedBytes > 0))
        {
            var file = Path.Combine(stored.TemporaryDirectory!, segment.TempFileName);
            Assert.True(new FileInfo(file).Length >= segment.DownloadedBytes);
        }

        var logBefore = _server.RangeLog.Count;
        await _harness.Coordinator.StartAsync([id]);
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(done.DestinationPath));

        // Resumed requests must start after offset 0 for segments that had data.
        var resumedRanges = _server.RangeLog.Skip(logBefore).Where(l => l.Contains("bytes=", StringComparison.Ordinal) && !l.EndsWith("bytes=0-0", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(resumedRanges, l => l.Contains("bytes=0-", StringComparison.Ordinal));
        Assert.False(Directory.Exists(stored.TemporaryDirectory));
    }

    [Fact]
    public async Task PauseOfNonResumableDownload_RestartsFromZero()
    {
        const long size = 2 * 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"norange/{size}?bps=400000"));
        await _harness.WaitForAsync(id, s => s.DownloadedBytes > 100_000);
        await _harness.Coordinator.PauseAsync([id]);

        var paused = _harness.Coordinator.Get(id)!;
        Assert.Equal(0, paused.DownloadedBytes);
        Assert.Contains("start over", paused.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplicationRestart_RestoresHistoryAndResumesPartialDownload()
    {
        const long size = 3 * 1024 * 1024;
        var completed = await _harness.AddAsync(_server.Url("file/1500"));
        await _harness.WaitForStatusAsync(completed, DownloadStatus.Completed);

        var partial = await _harness.AddAsync(_server.Url($"file/{size}?bps=500000"));
        await _harness.WaitForAsync(partial, s => s.DownloadedBytes > 300_000);
        await _harness.Coordinator.StopQueueAsync();
        var queued = await _harness.AddAsync(_server.Url("file/2500"), startNow: false);

        // Shutdown marks running transfers Interrupted; queue state survives.
        await _harness.RestartAsync();

        var restoredComplete = _harness.Coordinator.Get(completed)!;
        var restoredPartial = _harness.Coordinator.Get(partial)!;
        var restoredQueued = _harness.Coordinator.Get(queued)!;
        Assert.Equal(DownloadStatus.Completed, restoredComplete.Status);
        Assert.False(restoredComplete.FileMissing);
        Assert.Contains(restoredPartial.Status, new[] { DownloadStatus.Paused, DownloadStatus.Interrupted });
        Assert.True(restoredPartial.DownloadedBytes > 0);
        Assert.Equal(DownloadStatus.Queued, restoredQueued.Status);
        Assert.False(_harness.Coordinator.IsQueueRunning);

        await _harness.Coordinator.StartAsync([partial]);
        var done = await _harness.WaitForTerminalAsync(partial);
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(done.DestinationPath));

        await _harness.Coordinator.StartQueueAsync();
        var queuedDone = await _harness.WaitForTerminalAsync(queued);
        Assert.Equal(DownloadStatus.Completed, queuedDone.Status);
    }

    [Fact]
    public async Task RestartWithAutoResume_ContinuesInterruptedDownload()
    {
        const long size = 2 * 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"file/{size}?bps=400000"));
        await _harness.WaitForAsync(id, s => s.DownloadedBytes > 200_000);

        await _harness.RestartAsync();
        Assert.Equal(DownloadStatus.Interrupted, _harness.Coordinator.Get(id)!.Status);

        await _harness.Settings.UpdateAsync(s => s.AutoResumeOnStartup = true);
        await _harness.RestartAsync();
        var done = await _harness.WaitForTerminalAsync(id);
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(done.DestinationPath));
    }

    [Fact]
    public async Task MissingPartialFile_IsNotTreatedAsResumable()
    {
        const long size = 2 * 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"file/{size}?bps=400000"));
        await _harness.WaitForAsync(id, s => s.DownloadedBytes > 200_000);
        await _harness.Coordinator.PauseAsync([id]);
        var stored = (await _harness.Repository.LoadAllAsync()).Single(i => i.Id == id);

        await _harness.RestartAsync();
        // Simulate partial data lost while the app was closed.
        foreach (var file in Directory.EnumerateFiles(stored.TemporaryDirectory!))
        {
            File.Delete(file);
        }

        await _harness.RestartAsync();
        var restored = _harness.Coordinator.Get(id)!;
        Assert.Equal(0, restored.DownloadedBytes);
        Assert.Equal(DownloadErrorKind.PartialDataMissing, restored.LastErrorKind);

        await _harness.Coordinator.StartAsync([id]);
        var done = await _harness.WaitForTerminalAsync(id);
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(done.DestinationPath));
    }

    [Fact]
    public async Task Cancel_RemovesPartialData()
    {
        var id = await _harness.AddAsync(_server.Url("file/3000000?bps=300000"));
        await _harness.WaitForAsync(id, s => s.DownloadedBytes > 100_000);
        var temp = Directory.GetDirectories(Path.Combine(_harness.DownloadDirectory, ".oledm-partial")).Single();

        await _harness.Coordinator.CancelAsync([id]);

        var canceled = _harness.Coordinator.Get(id)!;
        Assert.Equal(DownloadStatus.Canceled, canceled.Status);
        Assert.False(Directory.Exists(temp));
        Assert.Empty(Directory.EnumerateFiles(_harness.DownloadDirectory));
    }

    [Fact]
    public async Task QueueConcurrencyLimit_IsNeverExceeded()
    {
        await _harness.Settings.UpdateAsync(s => s.MaxActiveDownloads = 2);
        var maxActive = 0;
        var gate = new object();
        _harness.Coordinator.DownloadUpdated += (_, _) =>
        {
            var active = _harness.Coordinator.GetAll().Count(s => s.Status.IsActive());
            lock (gate)
            {
                maxActive = Math.Max(maxActive, active);
            }
        };

        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await _harness.AddAsync(_server.Url($"file/300000?bps=1000000&seed={i + 1}"), startNow: i % 2 == 0,
                customize: r => r with { FileName = $"q{i}.bin" }));
        }

        foreach (var id in ids)
        {
            var done = await _harness.WaitForTerminalAsync(id);
            Assert.Equal(DownloadStatus.Completed, done.Status);
        }

        Assert.InRange(maxActive, 1, 2);
    }

    [Fact]
    public async Task QueuePriority_HighPriorityStartsFirst()
    {
        await _harness.Coordinator.StopQueueAsync();
        await _harness.Settings.UpdateAsync(s => s.MaxActiveDownloads = 1);
        var low = await _harness.AddAsync(_server.Url("file/1000"), startNow: false, r => r with { FileName = "low.bin", Priority = DownloadPriority.Low });
        var high = await _harness.AddAsync(_server.Url("file/1001"), startNow: false, r => r with { FileName = "high.bin", Priority = DownloadPriority.High });

        await _harness.Coordinator.StartQueueAsync();
        var lowDone = await _harness.WaitForTerminalAsync(low);
        var highDone = await _harness.WaitForTerminalAsync(high);
        Assert.True(highDone.StartedAt <= lowDone.StartedAt);
    }

    [Fact]
    public async Task ScheduledDownload_StartsAtItsTime()
    {
        await _harness.Coordinator.StopQueueAsync();
        var id = await _harness.AddAsync(_server.Url("file/1200"), startNow: false,
            r => r with { ScheduledAt = DateTimeOffset.UtcNow.AddSeconds(1.5) });

        await Task.Delay(500);
        Assert.Equal(DownloadStatus.Queued, _harness.Coordinator.Get(id)!.Status);
        var done = await _harness.WaitForTerminalAsync(id, TimeSpan.FromSeconds(15));
        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Contains(_harness.Notifications.Items, n => n.Title.Contains("Scheduled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GlobalSpeedLimit_IsEnforced()
    {
        await _harness.Settings.UpdateAsync(s => s.GlobalSpeedLimit = 512 * 1024);
        const long size = 1536 * 1024;
        var watch = Stopwatch.StartNew();
        var a = await _harness.AddAsync(_server.Url($"file/{size}"), customize: r => r with { FileName = "a.bin" });
        var b = await _harness.AddAsync(_server.Url($"file/{size}?seed=2"), customize: r => r with { FileName = "b.bin" });
        await _harness.WaitForTerminalAsync(a);
        await _harness.WaitForTerminalAsync(b);
        watch.Stop();

        // 3 MiB total at 512 KiB/s is about 6 s; allow for the initial burst and scheduling jitter.
        Assert.InRange(watch.Elapsed.TotalSeconds, 4.5, 12);
    }

    [Fact]
    public async Task PerDownloadSpeedLimit_IsEnforcedAcrossSegments()
    {
        const long size = 1024 * 1024;
        var watch = Stopwatch.StartNew();
        var id = await _harness.AddAsync(_server.Url($"file/{size}"), customize: r => r with { SpeedLimit = 256 * 1024, MaxConnections = 8 });
        var done = await _harness.WaitForTerminalAsync(id);
        watch.Stop();

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.InRange(watch.Elapsed.TotalSeconds, 3.0, 8);
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(done.DestinationPath));
    }

    [Fact]
    public async Task UnavailableDestination_WaitsAndResumesWhenItReturns()
    {
        var letter = FindFreeDriveLetter();
        Assert.SkipWhen(letter is null, "No free drive letter for SUBST.");
        var backing = Directory.CreateDirectory(Path.Combine(_harness.Root, "external")).FullName;
        var drive = $"{letter}:";

        var id = await _harness.AddAsync(_server.Url("file/200000"), startNow: false, r => r with { DestinationDirectory = drive + "\\Downloads" });
        await _harness.Coordinator.StartAsync([id]);
        var waiting = await _harness.WaitForStatusAsync(id, DownloadStatus.WaitingForDestination);
        Assert.Equal(DownloadErrorKind.DestinationUnavailable, waiting.LastErrorKind);
        Assert.Contains(_harness.Notifications.Items, n => n.Title.Contains("Destination", StringComparison.Ordinal));

        RunSubst($"{drive} \"{backing}\"");
        try
        {
            var done = await _harness.WaitForTerminalAsync(id, TimeSpan.FromSeconds(30));
            Assert.Equal(DownloadStatus.Completed, done.Status);
            Assert.Equal(TestContent.Sha256(200000), TestContent.FileSha256(Path.Combine(backing, "Downloads", done.FileName)));
        }
        finally
        {
            RunSubst($"{drive} /D");
        }
    }

    [Fact]
    public async Task FailedDownload_StateIsConsistentAfterReload()
    {
        var id = await _harness.AddAsync(_server.Url("status/403"));
        await _harness.WaitForStatusAsync(id, DownloadStatus.Failed);
        await _harness.RestartAsync();

        var restored = _harness.Coordinator.Get(id)!;
        Assert.Equal(DownloadStatus.Failed, restored.Status);
        Assert.Equal(DownloadErrorKind.Forbidden, restored.LastErrorKind);
        Assert.Equal(403, restored.HttpStatusCode);

        await _harness.Coordinator.RetryAsync([id]);
        await _harness.WaitForAsync(id, s => s.Status == DownloadStatus.Failed && s.RetryCount == 0);
    }

    [Fact]
    public async Task TrashAndRestore_KeepsRecord()
    {
        var id = await _harness.AddAsync(_server.Url("file/1000"));
        await _harness.WaitForStatusAsync(id, DownloadStatus.Completed);
        await _harness.Coordinator.MoveToTrashAsync([id]);
        Assert.True(_harness.Coordinator.Get(id)!.IsDeleted);

        await _harness.Coordinator.RestoreFromTrashAsync([id]);
        Assert.False(_harness.Coordinator.Get(id)!.IsDeleted);

        await _harness.Coordinator.DeletePermanentlyAsync([id]);
        Assert.Null(_harness.Coordinator.Get(id));
        Assert.True(File.Exists(Path.Combine(_harness.DownloadDirectory, "1000")), "Completed files are kept when history is deleted.");
    }

    [Fact]
    public async Task SensitiveHeaders_AreNotStoredInDatabase()
    {
        var id = await _harness.AddAsync(_server.Url("file/1000"), customize: r => r with
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret-token", ["X-Custom"] = "visible" },
            RememberSecrets = true,
        });
        await _harness.WaitForStatusAsync(id, DownloadStatus.Completed);

        var dbBytes = await ReadSharedAsync(Path.Combine(_harness.Root, "oledm.db"));
        var walPath = Path.Combine(_harness.Root, "oledm.db-wal");
        var walBytes = File.Exists(walPath) ? await ReadSharedAsync(walPath) : [];
        var text = System.Text.Encoding.UTF8.GetString(dbBytes) + System.Text.Encoding.UTF8.GetString(walBytes);
        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
        Assert.Contains(_harness.Credentials.Secrets.Values, v => v.Contains("secret-token", StringComparison.Ordinal));
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private static char? FindFreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var c = 'Q'; c <= 'Y'; c++)
        {
            if (!used.Contains(c))
            {
                return c;
            }
        }

        return null;
    }

    private static void RunSubst(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("subst.exe", arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit(10_000);
    }
}
