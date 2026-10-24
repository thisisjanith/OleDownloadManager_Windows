using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Models;

namespace OleDownloadManager.Engine;

/// <summary>
/// Pure queue-selection logic: decides which waiting downloads may start now. Kept free of
/// side effects so priority, scheduling and concurrency rules are easy to test.
/// </summary>
public static class QueueScheduler
{
    /// <param name="items">All downloads.</param>
    /// <param name="activeCount">Downloads currently holding a transfer slot.</param>
    /// <param name="manualStarts">Downloads the user explicitly started; they bypass a stopped queue but not the slot limit.</param>
    /// <param name="queueRunning">Whether the queue is processing ordinary waiting downloads.</param>
    /// <param name="maxActive">Global limit of simultaneous downloads.</param>
    /// <param name="now">Current time, for scheduled downloads.</param>
    public static IReadOnlyList<DownloadItem> SelectToStart(IEnumerable<DownloadItem> items, int activeCount,
        IReadOnlySet<Guid> manualStarts, bool queueRunning, int maxActive, DateTimeOffset now)
    {
        var free = Math.Max(0, maxActive - activeCount);
        if (free == 0)
        {
            return [];
        }

        return items
            .Where(i => i.Status == DownloadStatus.Queued && i.DeletedAt is null && IsEligible(i, manualStarts, queueRunning, now))
            .OrderByDescending(i => manualStarts.Contains(i.Id))
            .ThenByDescending(i => i.Priority)
            .ThenBy(i => i.QueuePosition)
            .ThenBy(i => i.CreatedAt)
            .Take(free)
            .ToList();
    }

    public static bool IsEligible(DownloadItem item, IReadOnlySet<Guid> manualStarts, bool queueRunning, DateTimeOffset now)
    {
        if (manualStarts.Contains(item.Id))
        {
            return true;
        }

        if (item.ScheduledAt is { } scheduled)
        {
            // Scheduled downloads start at their time even when the queue is stopped.
            return scheduled <= now;
        }

        return queueRunning;
    }

    /// <summary>The earliest future scheduled time among waiting downloads, if any.</summary>
    public static DateTimeOffset? NextScheduledTime(IEnumerable<DownloadItem> items, DateTimeOffset now) =>
        items.Where(i => i.Status == DownloadStatus.Queued && i.DeletedAt is null && i.ScheduledAt > now)
            .Select(i => i.ScheduledAt)
            .Min();

    /// <summary>
    /// True when the wall-clock time <paramref name="target"/> (in <paramref name="current"/>'s offset,
    /// normally local time) was crossed between two checks.
    /// </summary>
    public static bool CrossedDailyTime(TimeOnly target, DateTimeOffset previous, DateTimeOffset current)
    {
        if (current <= previous)
        {
            return false;
        }

        // Check today and yesterday's occurrence to handle midnight wrap-around.
        for (var dayOffset = 0; dayOffset >= -1; dayOffset--)
        {
            var day = DateOnly.FromDateTime(current.DateTime).AddDays(dayOffset);
            var occurrence = new DateTimeOffset(day.ToDateTime(target), current.Offset);
            if (occurrence > previous && occurrence <= current)
            {
                return true;
            }
        }

        return false;
    }
}
