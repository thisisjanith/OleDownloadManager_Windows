using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Http;
using OleDownloadManager.IntegrationTests.Infrastructure;

namespace OleDownloadManager.IntegrationTests;

/// <summary>End-to-end transfers through the real coordinator, engine, SQLite and file system.</summary>
public sealed class DownloadEngineTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private EngineHarness _harness = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        _harness = await EngineHarness.CreateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task SmallFile_DownloadsExactBytes()
    {
        var id = await _harness.AddAsync(_server.Url("file/1000"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(1000, done.ExpectedSize);
        Assert.Equal(TestContent.Sha256(1000), TestContent.FileSha256(done.DestinationPath));
        Assert.Equal("1000", done.FileName);
    }

    [Fact]
    public async Task LargeFile_UsesMultipleSegments_AndMatchesSingleConnectionOutput()
    {
        const long size = 6 * 1024 * 1024 + 123;
        _server.ResetConcurrency();
        var segmented = await _harness.AddAsync(_server.Url($"file/{size}?bps=4000000"), customize: r => r with { FileName = "segmented.bin", MaxConnections = 4 });
        var segmentedDone = await _harness.WaitForTerminalAsync(segmented);
        var maxSegments = _harness.History(segmented).Max(s => s.Segments.Count);
        var concurrency = _server.MaxConcurrentBodies;

        var single = await _harness.AddAsync(_server.Url($"file/{size}"), customize: r => r with { FileName = "single.bin", AccelerationEnabled = false });
        var singleDone = await _harness.WaitForTerminalAsync(single);

        Assert.Equal(DownloadStatus.Completed, segmentedDone.Status);
        Assert.Equal(DownloadStatus.Completed, singleDone.Status);
        Assert.True(maxSegments >= 4, $"expected >= 4 segments, saw {maxSegments}");
        Assert.True(concurrency >= 2, $"expected parallel range requests, saw {concurrency}");
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(segmentedDone.DestinationPath));
        Assert.Equal(TestContent.FileSha256(singleDone.DestinationPath), TestContent.FileSha256(segmentedDone.DestinationPath));
        Assert.True(segmentedDone.SupportsRangeRequests);
    }

    [Fact]
    public async Task ServerIgnoringRange_FallsBackToSingleConnection()
    {
        const long size = 2 * 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"norange/{size}"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.False(done.SupportsRangeRequests);
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(done.DestinationPath));
        // The probe response was reused, so the body was requested only once.
        Assert.Single(_server.RangeLog, l => l.StartsWith("/norange", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidContentRange_IsRejected_AndFallsBackSafely()
    {
        const long size = 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"badrange/{size}"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.False(done.SupportsRangeRequests);
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(done.DestinationPath));
        Assert.Contains("single connection", done.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownLength_Downloads_AndRecordsFinalSize()
    {
        const long size = 300_000;
        var id = await _harness.AddAsync(_server.Url($"unknown/{size}"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(size, done.ExpectedSize);
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(done.DestinationPath));
    }

    [Fact]
    public async Task Redirect_IsFollowed_AndFinalUrlRecorded()
    {
        var id = await _harness.AddAsync(_server.Url("redirect/5000"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.EndsWith("/file/5000", done.FinalUrl!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(TestContent.Sha256(5000), TestContent.FileSha256(done.DestinationPath));
    }

    [Fact]
    public async Task RedirectLoop_FailsWithClearError()
    {
        var id = await _harness.AddAsync(_server.Url("redirect-loop"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Failed, done.Status);
        Assert.Contains("redirected too many times", done.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotFound_FailsWithoutRetrying()
    {
        var id = await _harness.AddAsync(_server.Url("status/404"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Failed, done.Status);
        Assert.Equal(DownloadErrorKind.NotFound, done.LastErrorKind);
        Assert.Equal(404, done.HttpStatusCode);
        Assert.Equal(0, done.RetryCount);
        Assert.Empty(Directory.EnumerateFiles(_harness.DownloadDirectory));
    }

    [Fact]
    public async Task TemporaryServerError_IsRetried_ThenSucceeds()
    {
        var id = await _harness.AddAsync(_server.Url("flaky/a/20000?failures=2"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(2, done.RetryCount);
        Assert.Contains(_harness.History(id), s => s.Status == DownloadStatus.Retrying);
        Assert.Equal(TestContent.Sha256(20000), TestContent.FileSha256(done.DestinationPath));
    }

    [Fact]
    public async Task PersistentServerError_FailsAfterMaxRetries()
    {
        var id = await _harness.AddAsync(_server.Url("status/503"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Failed, done.Status);
        Assert.Equal(DownloadErrorKind.ServerError, done.LastErrorKind);
        Assert.Equal(3, done.RetryCount);
    }

    [Fact]
    public async Task InterruptedConnections_AreResumedWithoutCorruption()
    {
        const long size = 2 * 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"interrupt/i1/{size}"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(done.DestinationPath));
        // Interrupted segments re-requested from a later offset rather than from their start.
        var ranges = _server.RangeLog.Where(l => l.StartsWith("/interrupt", StringComparison.Ordinal)).ToList();
        Assert.True(ranges.Count >= 4, string.Join(", ", ranges));
    }

    [Fact]
    public async Task ContentDisposition_SetsName_AndPathTraversalIsNeutralized()
    {
        var plain = await _harness.AddAsync(_server.Url("disposition/100?name=quarterly%20report.pdf"));
        var traversal = await _harness.AddAsync(_server.Url("disposition/100?name=..%2F..%2Fevil.exe"));
        var unicode = await _harness.AddAsync(_server.Url("disposition/100?star=%E6%97%A5%E6%9C%AC%E8%AA%9E.txt"));

        var plainDone = await _harness.WaitForTerminalAsync(plain);
        var traversalDone = await _harness.WaitForTerminalAsync(traversal);
        var unicodeDone = await _harness.WaitForTerminalAsync(unicode);

        Assert.Equal("quarterly report.pdf", plainDone.FileName);
        Assert.Equal("evil.exe", traversalDone.FileName);
        Assert.Equal(_harness.DownloadDirectory, Path.GetDirectoryName(traversalDone.DestinationPath));
        Assert.Equal("日本語.txt", unicodeDone.FileName);
        Assert.True(File.Exists(unicodeDone.DestinationPath));
    }

    [Fact(Timeout = 60_000)]
    public async Task ManyConcurrentServerNamedDownloads_DoNotDeadlock()
    {
        // Regression: resolving server-supplied names consults every other download; doing that while
        // holding a download's own lock deadlocked concurrent jobs.
        await _harness.Settings.UpdateAsync(s => s.MaxActiveDownloads = 12, TestContext.Current.CancellationToken);
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            ids.Add(await _harness.AddAsync(_server.Url($"disposition/2000?name=same-name.bin&seed={i}")));
        }

        foreach (var id in ids)
        {
            Assert.Equal(DownloadStatus.Completed, (await _harness.WaitForTerminalAsync(id, TimeSpan.FromSeconds(45))).Status);
        }

        var names = ids.Select(id => _harness.Coordinator.Get(id)!.FileName).ToList();
        Assert.Equal(12, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("same-name.bin", names);
    }

    [Fact]
    public async Task ExistingFile_IsNeverOverwritten_ByDefault()
    {
        var existing = Path.Combine(_harness.DownloadDirectory, "2000");
        await File.WriteAllTextAsync(existing, "keep me");

        var id = await _harness.AddAsync(_server.Url("file/2000"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal("2000 (1)", done.FileName);
        Assert.Equal("keep me", await File.ReadAllTextAsync(existing));
        Assert.Equal(TestContent.Sha256(2000), TestContent.FileSha256(done.DestinationPath));
    }

    [Fact]
    public async Task ConflictPolicies_SkipAndReplace()
    {
        var existing = Path.Combine(_harness.DownloadDirectory, "3000");
        await File.WriteAllTextAsync(existing, "old");

        var skip = await _harness.Coordinator.AddAsync(new() { Url = _server.Url("file/3000"), StartNow = true, ConflictPolicy = ConflictPolicy.Skip });
        Assert.False(skip.Accepted);

        var replace = await _harness.AddAsync(_server.Url("file/3000"), customize: r => r with { ConflictPolicy = ConflictPolicy.Replace });
        var done = await _harness.WaitForTerminalAsync(replace);
        Assert.Equal("3000", done.FileName);
        Assert.Equal(TestContent.Sha256(3000), TestContent.FileSha256(existing));
    }

    [Fact]
    public async Task ZeroLengthFile_Completes()
    {
        var id = await _harness.AddAsync(_server.Url("file/0"), customize: r => r with { FileName = "empty.txt" });
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(0, new FileInfo(done.DestinationPath).Length);
    }

    [Fact]
    public async Task GzipContentEncoding_IsDecoded()
    {
        var id = await _harness.AddAsync(_server.Url("gzip/50000"), customize: r => r with { FileName = "data.bin" });
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(TestContent.Sha256(50000), TestContent.FileSha256(done.DestinationPath));
    }

    [Fact]
    public async Task ChecksumMismatch_FailsAndDoesNotSaveFile()
    {
        var id = await _harness.AddAsync(_server.Url("file/4000"), customize: r => r with { ExpectedSha256 = new string('a', 64) });
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Failed, done.Status);
        Assert.Equal(DownloadErrorKind.ChecksumMismatch, done.LastErrorKind);
        Assert.False(File.Exists(done.DestinationPath));
    }

    [Fact]
    public async Task ChecksumMatch_Completes_WithHash()
    {
        var expected = TestContent.Sha256(4000);
        var id = await _harness.AddAsync(_server.Url("file/4000"), customize: r => r with { ExpectedSha256 = expected.ToUpperInvariant() });
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(expected, done.Sha256);
    }

    [Fact]
    public async Task RateLimited429_UsesRetryAfter_AndCompletes()
    {
        const long size = 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"throttle429/t1/{size}"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(TestContent.Sha256(size), TestContent.FileSha256(done.DestinationPath));
    }

    [Fact]
    public async Task SegmentLayout_IsContiguous_WhileDownloading()
    {
        const long size = 3 * 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"file/{size}?bps=3000000"));
        await _harness.WaitForTerminalAsync(id);

        foreach (var snapshot in _harness.History(id).Where(s => s.Segments.Count > 0))
        {
            long expected = 0;
            foreach (var segment in snapshot.Segments.OrderBy(s => s.StartOffset))
            {
                Assert.Equal(expected, segment.StartOffset);
                Assert.InRange(segment.DownloadedBytes, 0, segment.Length);
                expected = segment.EndOffset + 1;
            }

            Assert.Equal(size, expected);
        }
    }

    [Fact]
    public async Task SourceChangedDuringDownload_RestartsAndNeverMixesVersions()
    {
        // Probe and the first segment requests see v1; later requests see v2 and must not be mixed in.
        const long size = 2 * 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"changing/c1/{size}?changeAfter=3&bps=2000000"));
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(TestContent.Sha256(size, seed: 2), TestContent.FileSha256(done.DestinationPath));
        Assert.Equal("\"v2\"", done.ETag);
        Assert.Contains("changed", done.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SourceChangedWhilePaused_ResumeRestartsFromScratch()
    {
        const long size = 2 * 1024 * 1024;
        var id = await _harness.AddAsync(_server.Url($"changing/c2/{size}?bps=400000"));
        await _harness.WaitForAsync(id, s => s.DownloadedBytes > 200_000);
        await _harness.Coordinator.PauseAsync([id]);
        _server.Versions["c2"] = 2;
        await _harness.Coordinator.StartAsync([id]);
        var done = await _harness.WaitForTerminalAsync(id);

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(TestContent.Sha256(size, seed: 2), TestContent.FileSha256(done.DestinationPath));
        Assert.Contains("changed", done.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SegmentCalculator_MatchesEngineBoundaries()
    {
        var ranges = SegmentCalculator.Calculate(10, 3, 1);
        Assert.Equal([(0L, 3L), (4L, 6L), (7L, 9L)], ranges);
    }
}
