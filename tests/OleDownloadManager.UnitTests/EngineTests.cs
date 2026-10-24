using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Engine;

namespace OleDownloadManager.UnitTests;

public sealed class RetryPolicyTests
{
    [Fact]
    public void BackoffGrowsExponentiallyAndIsCapped()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), jitterSource: () => 0.5);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.GetDelay(10));
    }

    [Fact]
    public void JitterStaysWithinTwentyPercent()
    {
        var low = new RetryPolicy(5, TimeSpan.FromSeconds(1), jitterSource: () => 0).GetDelay(1);
        var high = new RetryPolicy(5, TimeSpan.FromSeconds(1), jitterSource: () => 1).GetDelay(1);
        Assert.Equal(TimeSpan.FromMilliseconds(800), low);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), high);
    }

    [Fact]
    public void RetryAfterHintWinsButIsCapped()
    {
        var policy = new RetryPolicy(5);
        Assert.Equal(TimeSpan.FromSeconds(7), policy.GetDelay(1, TimeSpan.FromSeconds(7)));
        Assert.Equal(RetryPolicy.MaxRetryAfter, policy.GetDelay(1, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void OnlyTransientErrorsAreRetried_UpToMax()
    {
        var policy = new RetryPolicy(2);
        var transient = new DownloadException(DownloadErrorKind.ServerError, "x", true);
        var permanent = new DownloadException(DownloadErrorKind.NotFound, "x", false);
        Assert.True(policy.CanRetry(1, transient));
        Assert.True(policy.CanRetry(2, transient));
        Assert.False(policy.CanRetry(3, transient));
        Assert.False(policy.CanRetry(1, permanent));
    }

    [Fact]
    public void ParsesRetryAfterDeltaAndDate()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var delta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        delta.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        using var date = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        date.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddMinutes(2));
        using var none = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.ParseRetryAfter(delta, now));
        Assert.Equal(TimeSpan.FromMinutes(2), RetryPolicy.ParseRetryAfter(date, now));
        Assert.Null(RetryPolicy.ParseRetryAfter(none, now));
    }
}

public sealed class SpeedMonitorTests
{
    [Fact]
    public void ReportsRollingWindowSpeed()
    {
        var monitor = new SpeedMonitor(TimeSpan.FromSeconds(2));
        for (var second = 0; second <= 10; second++)
        {
            // 1 MB/s for 5 s, then 100 KB/s.
            var bytes = second <= 5 ? second * 1_000_000L : 5_000_000L + ((second - 5) * 100_000L);
            monitor.AddSample(TimeSpan.FromSeconds(second), bytes);
        }

        Assert.InRange(monitor.CurrentSpeed, 99_000, 101_000);
        Assert.InRange(monitor.AverageSpeed, 549_000, 551_000);
    }

    [Fact]
    public void ReturnsZeroWithoutEnoughData()
    {
        var monitor = new SpeedMonitor();
        Assert.Equal(0, monitor.CurrentSpeed);
        monitor.AddSample(TimeSpan.Zero, 100);
        Assert.Equal(0, monitor.CurrentSpeed);
        monitor.AddSample(TimeSpan.Zero, 200);
        Assert.Equal(0, monitor.CurrentSpeed);
    }
}

public sealed class BandwidthLimiterTests
{
    [Fact]
    public void UnlimitedNeverWaits()
    {
        var limiter = new BandwidthLimiter(null);
        Assert.True(limiter.ConsumeAsync(100_000_000, CancellationToken.None).IsCompleted);
        Assert.Null(limiter.Limit);
    }

    [Fact]
    public void BurstWithinCapacityDoesNotWait_ButDebtDoes()
    {
        var limiter = new BandwidthLimiter(100_000);
        Assert.True(limiter.ConsumeAsync(1_000, CancellationToken.None).IsCompleted);
        Assert.False(limiter.ConsumeAsync(200_000, CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task EnforcesRateWithinTolerance()
    {
        var limiter = new BandwidthLimiter(256 * 1024);
        var watch = Stopwatch.StartNew();
        // Two concurrent consumers share one limit: 768 KiB total.
        await Task.WhenAll(Consume(limiter, 384 * 1024), Consume(limiter, 384 * 1024));
        watch.Stop();
        Assert.InRange(watch.Elapsed.TotalSeconds, 2.4, 4.0);
    }

    [Fact]
    public void PreferredChunkScalesWithRate()
    {
        Assert.Equal(65536, new BandwidthLimiter(null).PreferredChunkSize(65536));
        Assert.Equal(8192, new BandwidthLimiter(65536).PreferredChunkSize(65536));
        Assert.Equal(1024, new BandwidthLimiter(100).PreferredChunkSize(65536));
    }

    private static async Task Consume(BandwidthLimiter limiter, int total)
    {
        for (var sent = 0; sent < total; sent += 16 * 1024)
        {
            await limiter.ConsumeAsync(16 * 1024, CancellationToken.None);
        }
    }
}

public sealed class QueueSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static DownloadItem Item(int position, DownloadPriority priority = DownloadPriority.Normal, DownloadStatus status = DownloadStatus.Queued, DateTimeOffset? scheduled = null) => new()
    {
        Id = Guid.NewGuid(),
        SourceUrl = new Uri("https://example.com/" + position),
        FileName = position.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DestinationDirectory = @"C:\d",
        QueuePosition = position,
        Priority = priority,
        Status = status,
        ScheduledAt = scheduled,
        CreatedAt = Now,
    };

    [Fact]
    public void RespectsFreeSlots()
    {
        var items = Enumerable.Range(1, 10).Select(i => Item(i)).ToList();
        Assert.Equal(3, QueueScheduler.SelectToStart(items, 0, new HashSet<Guid>(), true, 3, Now).Count);
        Assert.Single(QueueScheduler.SelectToStart(items, 2, new HashSet<Guid>(), true, 3, Now));
        Assert.Empty(QueueScheduler.SelectToStart(items, 3, new HashSet<Guid>(), true, 3, Now));
        Assert.Empty(QueueScheduler.SelectToStart(items, 5, new HashSet<Guid>(), true, 3, Now));
    }

    [Fact]
    public void OrdersByManualThenPriorityThenPosition()
    {
        var a = Item(1);
        var b = Item(2, DownloadPriority.High);
        var c = Item(3, DownloadPriority.Low);
        var d = Item(4);
        var manual = new HashSet<Guid> { c.Id };
        var picked = QueueScheduler.SelectToStart([a, b, c, d], 0, manual, true, 4, Now);
        Assert.Equal([c.Id, b.Id, a.Id, d.Id], picked.Select(i => i.Id));
    }

    [Fact]
    public void StoppedQueueStartsOnlyManualAndDueScheduledItems()
    {
        var ordinary = Item(1);
        var manual = Item(2);
        var due = Item(3, scheduled: Now.AddMinutes(-1));
        var future = Item(4, scheduled: Now.AddMinutes(10));
        var picked = QueueScheduler.SelectToStart([ordinary, manual, due, future], 0, new HashSet<Guid> { manual.Id }, false, 10, Now);
        Assert.Equal(new[] { manual.Id, due.Id }.ToHashSet(), picked.Select(i => i.Id).ToHashSet());
    }

    [Fact]
    public void IgnoresNonQueuedAndTrashedItems()
    {
        var paused = Item(1, status: DownloadStatus.Paused);
        var trashed = Item(2);
        trashed.DeletedAt = Now;
        var failed = Item(3, status: DownloadStatus.Failed);
        Assert.Empty(QueueScheduler.SelectToStart([paused, trashed, failed], 0, new HashSet<Guid> { paused.Id }, true, 5, Now));
    }

    [Fact]
    public void FindsNextScheduledTime()
    {
        var items = new[] { Item(1, scheduled: Now.AddHours(2)), Item(2, scheduled: Now.AddMinutes(5)), Item(3) };
        Assert.Equal(Now.AddMinutes(5), QueueScheduler.NextScheduledTime(items, Now));
    }

    [Theory]
    [InlineData("2026-05-01T21:59:00+00:00", "2026-05-01T22:00:30+00:00", true)]
    [InlineData("2026-05-01T22:00:30+00:00", "2026-05-01T22:01:00+00:00", false)]
    [InlineData("2026-05-01T23:59:00+00:00", "2026-05-02T00:01:00+00:00", false)]
    public void DetectsDailyTimeCrossing(string previous, string current, bool expected)
    {
        Assert.Equal(expected, QueueScheduler.CrossedDailyTime(new TimeOnly(22, 0), DateTimeOffset.Parse(previous, System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse(current, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void DetectsCrossingAcrossMidnight()
    {
        var previous = new DateTimeOffset(2026, 5, 1, 23, 59, 0, TimeSpan.Zero);
        var current = new DateTimeOffset(2026, 5, 2, 0, 1, 0, TimeSpan.Zero);
        Assert.True(QueueScheduler.CrossedDailyTime(new TimeOnly(0, 0), previous, current));
    }
}

public sealed class ErrorClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DownloadErrorKind.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, DownloadErrorKind.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, DownloadErrorKind.NotFound, false)]
    [InlineData(HttpStatusCode.RequestTimeout, DownloadErrorKind.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, DownloadErrorKind.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, DownloadErrorKind.ServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DownloadErrorKind.ServerError, true)]
    [InlineData(HttpStatusCode.NotImplemented, DownloadErrorKind.ServerError, false)]
    [InlineData(HttpStatusCode.BadRequest, DownloadErrorKind.HttpError, false)]
    public void MapsStatusCodes(HttpStatusCode status, DownloadErrorKind kind, bool transient)
    {
        var error = DownloadErrorClassifier.FromStatusCode(status);
        Assert.Equal(kind, error.Kind);
        Assert.Equal(transient, error.IsTransient);
        Assert.Equal((int)status, error.HttpStatusCode);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, DownloadErrorKind.DnsFailure)]
    [InlineData(SocketError.TimedOut, DownloadErrorKind.ConnectionTimeout)]
    [InlineData(SocketError.ConnectionReset, DownloadErrorKind.ConnectionReset)]
    [InlineData(SocketError.NetworkUnreachable, DownloadErrorKind.NetworkUnavailable)]
    [InlineData(SocketError.ConnectionRefused, DownloadErrorKind.ConnectionFailed)]
    public void MapsSocketErrors(SocketError socketError, DownloadErrorKind kind)
    {
        var error = DownloadErrorClassifier.Classify(new HttpRequestException("x", new SocketException((int)socketError)));
        Assert.Equal(kind, error.Kind);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public void MapsTlsFailures()
    {
        var error = DownloadErrorClassifier.Classify(new HttpRequestException("x", new System.Security.Authentication.AuthenticationException("bad cert")));
        Assert.Equal(DownloadErrorKind.TlsFailure, error.Kind);
        Assert.False(error.IsTransient);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070), DownloadErrorKind.DiskFull)]
    [InlineData(unchecked((int)0x80070027), DownloadErrorKind.DiskFull)]
    [InlineData(unchecked((int)0x80070005), DownloadErrorKind.AccessDenied)]
    [InlineData(unchecked((int)0x80070015), DownloadErrorKind.DestinationUnavailable)]
    [InlineData(unchecked((int)0x8007048F), DownloadErrorKind.DestinationUnavailable)]
    [InlineData(unchecked((int)0x80070037), DownloadErrorKind.DestinationUnavailable)]
    [InlineData(unchecked((int)0x80070003), DownloadErrorKind.DestinationUnavailable)]
    public void MapsFileSystemErrors(int hresult, DownloadErrorKind kind)
    {
        Assert.Equal(kind, DownloadErrorClassifier.ClassifyFileSystem(new IOException("x", hresult)).Kind);
    }

    [Fact]
    public void MapsUnauthorizedAccess()
    {
        Assert.Equal(DownloadErrorKind.AccessDenied, DownloadErrorClassifier.Classify(new UnauthorizedAccessException()).Kind);
        Assert.Equal(DownloadErrorKind.DestinationUnavailable, DownloadErrorClassifier.Classify(new DirectoryNotFoundException()).Kind);
    }

    [Fact]
    public void IncompleteChunkedResponseIsTransient()
    {
        var error = DownloadErrorClassifier.Classify(new HttpIOException(HttpRequestError.ResponseEnded, "ended"));
        Assert.Equal(DownloadErrorKind.IncompleteResponse, error.Kind);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public void PipeTypesAreAvailable() => Assert.NotNull(typeof(NamedPipeClientStream));
}

public sealed class ResumeManagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "oledm-ut-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private DownloadItem Resumable(params (long Start, long End, long Durable)[] segments)
    {
        var temp = Directory.CreateDirectory(Path.Combine(_dir, ".oledm-partial", "x")).FullName;
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            SourceUrl = new Uri("https://example.com/f"),
            FileName = "f",
            DestinationDirectory = _dir,
            TemporaryDirectory = temp,
            SupportsRangeRequests = true,
            ExpectedSize = segments[^1].End + 1,
            Status = DownloadStatus.Paused,
        };
        for (var i = 0; i < segments.Length; i++)
        {
            item.Segments.Add(new DownloadSegment { Index = i, StartOffset = segments[i].Start, EndOffset = segments[i].End, DownloadedBytes = segments[i].Durable, DurableBytes = segments[i].Durable });
        }

        return item;
    }

    [Fact]
    public void KeepsProgressWhenFilesMatch()
    {
        var item = Resumable((0, 99, 50), (100, 199, 100));
        File.WriteAllBytes(Path.Combine(item.TemporaryDirectory!, "seg-000.part"), new byte[50]);
        File.WriteAllBytes(Path.Combine(item.TemporaryDirectory!, "seg-001.part"), new byte[120]);

        Assert.False(ResumeManager.VerifyPartialData(item));
        Assert.Equal(150, item.DownloadedBytes);
        Assert.Equal(SegmentState.Completed, item.Segments[1].State);
    }

    [Fact]
    public void FlagsMissingOrShortFiles()
    {
        var item = Resumable((0, 99, 50), (100, 199, 80));
        File.WriteAllBytes(Path.Combine(item.TemporaryDirectory!, "seg-001.part"), new byte[30]);

        Assert.True(ResumeManager.VerifyPartialData(item));
        Assert.Equal(0, item.Segments[0].DownloadedBytes);
        Assert.Equal(30, item.Segments[1].DownloadedBytes);
        Assert.Equal(30, item.DownloadedBytes);
    }

    [Fact]
    public void StartupMarksActiveDownloadsInterrupted()
    {
        var item = Resumable((0, 99, 0));
        item.Status = DownloadStatus.Downloading;
        var result = ResumeManager.ReconcileOnStartup(item);
        Assert.True(result.WasActive);
        Assert.Equal(DownloadStatus.Interrupted, item.Status);
    }

    [Fact]
    public void StartupFlagsMissingCompletedFile()
    {
        var item = Resumable((0, 99, 100));
        item.Status = DownloadStatus.Completed;
        ResumeManager.ReconcileOnStartup(item);
        Assert.True(item.FileMissing);
    }

    [Fact]
    public void StartupDetectsUnavailableVolume()
    {
        var item = Resumable((0, 99, 10));
        item.DestinationDirectory = @"\\nonexistent-host-oledm\share\dl";
        item.Status = DownloadStatus.Interrupted;
        var result = ResumeManager.ReconcileOnStartup(item);
        Assert.True(result.DestinationUnavailable);
        Assert.Equal(DownloadStatus.WaitingForDestination, item.Status);
    }

    [Fact]
    public void NonResumableProgressIsDiscarded()
    {
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            SourceUrl = new Uri("https://e.com/f"),
            FileName = "f",
            DestinationDirectory = _dir,
            SupportsRangeRequests = false,
            DownloadedBytes = 500,
            Status = DownloadStatus.Paused,
        };
        Assert.True(ResumeManager.VerifyPartialData(item));
        Assert.Equal(0, item.DownloadedBytes);
    }
}
