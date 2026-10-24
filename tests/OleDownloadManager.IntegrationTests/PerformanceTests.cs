using System.Diagnostics;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Engine;
using OleDownloadManager.IntegrationTests.Infrastructure;

namespace OleDownloadManager.IntegrationTests;

/// <summary>
/// Large-file behaviour: data is streamed to disk, so memory must not grow with file size.
/// Throughput and CPU are reported (not asserted) because they depend on the machine.
/// </summary>
public sealed class PerformanceTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private EngineHarness _harness = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        var options = EngineHarness.FastOptions();
        options.MinimumSegmentSize = SegmentCalculatorDefaults;
        options.BufferSize = 64 * 1024;
        options.ProgressInterval = TimeSpan.FromMilliseconds(250);
        options.CheckpointInterval = TimeSpan.FromSeconds(3);
        _harness = await EngineHarness.CreateAsync(options: options);
    }

    private const long SegmentCalculatorDefaults = 1024 * 1024;

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task HalfGigabyteDownload_StreamsWithBoundedMemory()
    {
        const long size = 512L * 1024 * 1024;
        using var process = Process.GetCurrentProcess();
        GC.Collect();
        process.Refresh();
        var privateBefore = process.PrivateMemorySize64;
        var cpuBefore = process.TotalProcessorTime;
        long peakPrivate = privateBefore;
        var watch = Stopwatch.StartNew();

        var id = await _harness.AddAsync(_server.Url($"fast/{size}"), customize: r => r with { FileName = "large.bin", MaxConnections = 4 });
        using var sampler = new Timer(_ =>
        {
            process.Refresh();
            peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
        }, null, 0, 200);
        var done = await _harness.WaitForTerminalAsync(id, TimeSpan.FromMinutes(5));
        watch.Stop();
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(size, new FileInfo(done.DestinationPath).Length);
        Assert.Equal(TestContent.FastSha256(size), TestContent.FileSha256(done.DestinationPath));

        var growthMb = (peakPrivate - privateBefore) / 1024.0 / 1024.0;
        var summary =
            $"512 MiB in {watch.Elapsed.TotalSeconds:N1}s ({size / 1024.0 / 1024.0 / watch.Elapsed.TotalSeconds:N0} MiB/s), " +
            $"CPU {cpu.TotalSeconds:N1}s (server and client in one process), peak private-memory growth {growthMb:N0} MiB";
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "oledm-performance.txt"), summary);
        // Kestrel, the client and file caches share this process; anything near the file size would mean buffering.
        Assert.True(growthMb < 150, $"Private memory grew by {growthMb:N0} MiB while downloading 512 MiB.");
    }
}
