using OleDownloadManager.Core.Models;

namespace OleDownloadManager.Core.Http;

/// <summary>Byte-range arithmetic for segmented downloads.</summary>
public static class SegmentCalculator
{
    /// <summary>Segments smaller than this are not worth an extra connection.</summary>
    public const long DefaultMinimumSegmentSize = 1024 * 1024;

    public const int MaxConnections = 8;

    /// <summary>
    /// Splits <paramref name="totalSize"/> bytes into contiguous, non-overlapping inclusive ranges.
    /// The first <c>totalSize % count</c> segments receive one extra byte.
    /// Returns an empty list for an empty file.
    /// </summary>
    public static IReadOnlyList<(long Start, long End)> Calculate(long totalSize, int requestedSegments, long minimumSegmentSize = DefaultMinimumSegmentSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedSegments, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumSegmentSize, 1);

        if (totalSize == 0)
        {
            return [];
        }

        var count = SegmentCountFor(totalSize, requestedSegments, minimumSegmentSize);
        var baseLength = totalSize / count;
        var remainder = totalSize % count;

        var result = new List<(long, long)>(count);
        long start = 0;
        for (var i = 0; i < count; i++)
        {
            var length = baseLength + (i < remainder ? 1 : 0);
            var end = start + length - 1;
            result.Add((start, end));
            start = end + 1;
        }

        return result;
    }

    public static int SegmentCountFor(long totalSize, int requestedSegments, long minimumSegmentSize = DefaultMinimumSegmentSize)
    {
        var requested = Math.Clamp(requestedSegments, 1, MaxConnections);
        var bySize = totalSize / minimumSegmentSize;
        return (int)Math.Clamp(Math.Min(requested, bySize), 1, MaxConnections);
    }

    public static List<DownloadSegment> CreateSegments(long totalSize, int requestedSegments, long minimumSegmentSize = DefaultMinimumSegmentSize) =>
        Calculate(totalSize, requestedSegments, minimumSegmentSize)
            .Select((r, i) => new DownloadSegment { Index = i, StartOffset = r.Start, EndOffset = r.End })
            .ToList();

    /// <summary>
    /// Splits the undownloaded remainder of <paramref name="segment"/> in half, shrinking it and
    /// returning the new tail range. Returns null when the remainder is too small to split.
    /// </summary>
    public static (long Start, long End)? TrySplitRemaining(long startOffset, long endOffset, long downloadedBytes, long minimumSegmentSize = DefaultMinimumSegmentSize)
    {
        var nextByte = startOffset + downloadedBytes;
        var remaining = endOffset - nextByte + 1;
        if (remaining < minimumSegmentSize * 2)
        {
            return null;
        }

        var newStart = nextByte + (remaining / 2);
        return (newStart, endOffset);
    }

    /// <summary>Checks that segments cover exactly [0, totalSize) with no gaps or overlaps.</summary>
    public static bool IsValidLayout(IEnumerable<DownloadSegment> segments, long totalSize)
    {
        long expectedStart = 0;
        foreach (var segment in segments.OrderBy(s => s.StartOffset))
        {
            if (segment.StartOffset != expectedStart || segment.EndOffset < segment.StartOffset ||
                segment.DownloadedBytes < 0 || segment.DownloadedBytes > segment.Length)
            {
                return false;
            }

            expectedStart = segment.EndOffset + 1;
        }

        return expectedStart == totalSize;
    }
}
