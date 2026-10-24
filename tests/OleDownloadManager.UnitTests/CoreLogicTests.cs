using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Formatting;
using OleDownloadManager.Core.Http;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.StateMachine;

namespace OleDownloadManager.UnitTests;

public sealed class StateMachineTests
{
    [Theory]
    [InlineData(DownloadStatus.Queued, DownloadStatus.Connecting)]
    [InlineData(DownloadStatus.Connecting, DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Downloading, DownloadStatus.Verifying)]
    [InlineData(DownloadStatus.Verifying, DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Downloading, DownloadStatus.Paused)]
    [InlineData(DownloadStatus.Downloading, DownloadStatus.Interrupted)]
    [InlineData(DownloadStatus.Downloading, DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Paused, DownloadStatus.Connecting)]
    [InlineData(DownloadStatus.Failed, DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Retrying, DownloadStatus.Connecting)]
    [InlineData(DownloadStatus.WaitingForDestination, DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Interrupted, DownloadStatus.Queued)]
    public void AllowsValidTransitions(DownloadStatus from, DownloadStatus to)
    {
        Assert.True(DownloadStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(DownloadStatus.Completed, DownloadStatus.Connecting)]
    [InlineData(DownloadStatus.Completed, DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Canceled, DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Canceled, DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Queued, DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Failed, DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Paused, DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Downloading, DownloadStatus.Downloading)]
    public void RejectsInvalidTransitions(DownloadStatus from, DownloadStatus to)
    {
        Assert.False(DownloadStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidStateTransitionException>(() => DownloadStateMachine.EnsureCanTransition(from, to));
    }

    [Fact]
    public void CompletedAndCanceledRestartOnlyWithExplicitAction()
    {
        Assert.False(DownloadStateMachine.CanTransition(DownloadStatus.Completed, DownloadStatus.Queued));
        Assert.True(DownloadStateMachine.CanTransition(DownloadStatus.Completed, DownloadStatus.Queued, explicitUserAction: true));
        Assert.True(DownloadStateMachine.CanTransition(DownloadStatus.Canceled, DownloadStatus.Queued, explicitUserAction: true));
        Assert.False(DownloadStateMachine.CanTransition(DownloadStatus.Completed, DownloadStatus.Downloading, explicitUserAction: true));
    }

    [Fact]
    public void EveryStatusHasATransitionEntry()
    {
        foreach (var status in Enum.GetValues<DownloadStatus>())
        {
            _ = DownloadStateMachine.CanTransition(status, DownloadStatus.Queued);
        }
    }
}

public sealed class SegmentCalculatorTests
{
    [Theory]
    [InlineData(1L, 4)]
    [InlineData(10L, 3)]
    [InlineData(1_000_003L, 8)]
    [InlineData(4_294_967_311L, 8)]
    [InlineData(10L * 1024 * 1024 * 1024 + 7, 5)]
    public void SegmentsCoverFileExactlyWithoutOverlap(long size, int count)
    {
        var ranges = SegmentCalculator.Calculate(size, count, minimumSegmentSize: 1);

        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(size - 1, ranges[^1].End);
        Assert.Equal(size, ranges.Sum(r => r.End - r.Start + 1));
        for (var i = 1; i < ranges.Count; i++)
        {
            Assert.Equal(ranges[i - 1].End + 1, ranges[i].Start);
            Assert.True(ranges[i].End >= ranges[i].Start);
        }
    }

    [Fact]
    public void RandomizedSizesAlwaysProduceValidLayouts()
    {
        var random = new Random(1234);
        for (var i = 0; i < 2000; i++)
        {
            var size = random.NextInt64(1, 50L * 1024 * 1024 * 1024);
            var count = random.Next(1, 9);
            var segments = SegmentCalculator.CreateSegments(size, count, minimumSegmentSize: random.Next(1, 4 * 1024 * 1024));
            Assert.True(SegmentCalculator.IsValidLayout(segments, size), $"size={size} count={count}");
            Assert.InRange(segments.Count, 1, 8);
        }
    }

    [Fact]
    public void SmallFilesUseOneConnection()
    {
        Assert.Single(SegmentCalculator.Calculate(500_000, 8));
        Assert.Equal(2, SegmentCalculator.Calculate(2 * SegmentCalculator.DefaultMinimumSegmentSize, 8).Count);
        Assert.Equal(8, SegmentCalculator.Calculate(100 * SegmentCalculator.DefaultMinimumSegmentSize, 8).Count);
    }

    [Fact]
    public void EmptyFileHasNoSegments() => Assert.Empty(SegmentCalculator.Calculate(0, 4));

    [Fact]
    public void ConnectionCountIsCappedAtEight() => Assert.Equal(8, SegmentCalculator.Calculate(1L << 40, 64, 1).Count);

    [Fact]
    public void SplitHalvesRemainingRange()
    {
        var split = SegmentCalculator.TrySplitRemaining(0, 9_999_999, 2_000_000, 1_000_000);
        Assert.Equal((6_000_000L, 9_999_999L), split);
        Assert.Null(SegmentCalculator.TrySplitRemaining(0, 1_000_000, 500_000, 1_000_000));
    }

    [Fact]
    public void DetectsInvalidLayouts()
    {
        var gap = new List<DownloadSegment>
        {
            new() { Index = 0, StartOffset = 0, EndOffset = 9 },
            new() { Index = 1, StartOffset = 11, EndOffset = 19 },
        };
        var overlap = new List<DownloadSegment>
        {
            new() { Index = 0, StartOffset = 0, EndOffset = 10 },
            new() { Index = 1, StartOffset = 10, EndOffset = 19 },
        };
        Assert.False(SegmentCalculator.IsValidLayout(gap, 20));
        Assert.False(SegmentCalculator.IsValidLayout(overlap, 20));
    }
}

public sealed class ContentRangeTests
{
    [Theory]
    [InlineData("bytes 0-499/1234", 0L, 499L, 1234L)]
    [InlineData("bytes 500-999/*", 500L, 999L, null)]
    [InlineData("BYTES 0-0/1", 0L, 0L, 1L)]
    [InlineData("bytes 42-42/9999999999999", 42L, 42L, 9999999999999L)]
    public void ParsesValidValues(string value, long first, long last, long? total)
    {
        Assert.True(ContentRange.TryParse(value, out var range));
        Assert.Equal(first, range.First);
        Assert.Equal(last, range.Last);
        Assert.Equal(total, range.CompleteLength);
        Assert.Equal(last - first + 1, range.Length);
    }

    [Fact]
    public void ParsesUnsatisfiedForm()
    {
        Assert.True(ContentRange.TryParse("bytes */1234", out var range));
        Assert.Null(range.First);
        Assert.Equal(1234, range.CompleteLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bytes")]
    [InlineData("items 0-1/2")]
    [InlineData("bytes 10-5/100")]
    [InlineData("bytes 0-100/100")]
    [InlineData("bytes -1-5/100")]
    [InlineData("bytes 0-/100")]
    [InlineData("bytes */*")]
    [InlineData("bytes 0-5")]
    [InlineData("bytes 0x0-5/10")]
    [InlineData("bytes 0-5/abc")]
    [InlineData("bytes 99999999999999999999-1/2")]
    public void RejectsInvalidValues(string? value)
    {
        Assert.False(ContentRange.TryParse(value, out _));
    }

    [Fact]
    public void MatchesRequestedRange()
    {
        Assert.True(ContentRange.TryParse("bytes 100-199/1000", out var range));
        Assert.True(range.Matches(100, 199, 1000));
        Assert.False(range.Matches(100, 200, 1000));
        Assert.False(range.Matches(100, 199, 999));
    }
}

public sealed class ProgressAndFormattingTests
{
    [Theory]
    [InlineData(0L, 100L, 0.0)]
    [InlineData(50L, 100L, 0.5)]
    [InlineData(150L, 100L, 1.0)]
    [InlineData(-5L, 100L, 0.0)]
    public void FractionIsClamped(long downloaded, long total, double expected)
    {
        Assert.Equal(expected, ProgressMath.Fraction(downloaded, total));
    }

    [Fact]
    public void UnknownSizeHasNoPercentage()
    {
        Assert.Null(ProgressMath.Fraction(1000, null));
        Assert.Equal(1, ProgressMath.Fraction(1000, null, completed: true));
        Assert.Equal(0, ProgressMath.Fraction(0, 0));
    }

    [Fact]
    public void EstimatesRemainingTimeSafely()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), ProgressMath.EstimateRemaining(0, 1000, 100));
        Assert.Null(ProgressMath.EstimateRemaining(0, null, 100));
        Assert.Null(ProgressMath.EstimateRemaining(0, 1000, 0));
        Assert.Null(ProgressMath.EstimateRemaining(0, 1000, double.NaN));
        Assert.Null(ProgressMath.EstimateRemaining(0, long.MaxValue, 1));
        Assert.Equal(TimeSpan.Zero, ProgressMath.EstimateRemaining(2000, 1000, 100));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.00 KB")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(10L * 1024 * 1024, "10.0 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.00 GB")]
    [InlineData(-1L, "—")]
    public void FormatsSizes(long bytes, string expected)
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal(expected, ByteFormatter.FormatSize(bytes));
    }

    [Theory]
    [InlineData(0d, "")]
    [InlineData(512d, "0.5 KB/s")]
    [InlineData(200d * 1024, "200 KB/s")]
    [InlineData(2.5 * 1024 * 1024, "2.5 MB/s")]
    [InlineData(double.PositiveInfinity, "")]
    public void FormatsSpeeds(double speed, string expected)
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal(expected, ByteFormatter.FormatSpeed(speed));
    }

    [Fact]
    public void FormatsDurations()
    {
        Assert.Equal("5s", ByteFormatter.FormatDuration(TimeSpan.FromSeconds(5)));
        Assert.Equal("2m 05s", ByteFormatter.FormatDuration(TimeSpan.FromSeconds(125)));
        Assert.Equal("1h 01m", ByteFormatter.FormatDuration(TimeSpan.FromMinutes(61)));
        Assert.Equal("2d 3h", ByteFormatter.FormatDuration(TimeSpan.FromHours(51)));
        Assert.Equal("", ByteFormatter.FormatDuration(null));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}

public sealed class CategoryClassifierTests
{
    [Theory]
    [InlineData("setup.exe", DownloadCategory.Applications)]
    [InlineData("SETUP.MSI", DownloadCategory.Applications)]
    [InlineData("a.zip", DownloadCategory.Archives)]
    [InlineData("a.tar.gz", DownloadCategory.Archives)]
    [InlineData("doc.pdf", DownloadCategory.Documents)]
    [InlineData("movie.mkv", DownloadCategory.Video)]
    [InlineData("song.flac", DownloadCategory.Audio)]
    [InlineData("pic.webp", DownloadCategory.Images)]
    [InlineData("data.bin", DownloadCategory.Other)]
    [InlineData("noextension", DownloadCategory.Other)]
    [InlineData(null, DownloadCategory.Other)]
    public void ClassifiesByExtension(string? name, DownloadCategory expected)
    {
        Assert.Equal(expected, new CategoryClassifier().Classify(name));
    }

    [Fact]
    public void UsesCustomMappings()
    {
        var classifier = new CategoryClassifier(new Dictionary<DownloadCategory, string[]> { [DownloadCategory.Video] = ["BIN", ".xyz"] });
        Assert.Equal(DownloadCategory.Video, classifier.Classify("a.bin"));
        Assert.Equal(DownloadCategory.Video, classifier.Classify("a.xyz"));
        Assert.Equal(DownloadCategory.Other, classifier.Classify("a.zip"));
    }
}
