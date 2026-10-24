namespace OleDownloadManager.Engine;

/// <summary>
/// Rolling-window transfer speed. Feed it cumulative byte counts with timestamps;
/// it reports the average rate over the most recent window.
/// </summary>
public sealed class SpeedMonitor
{
    private readonly TimeSpan _window;
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private readonly object _lock = new();
    private long _firstTicks = -1;
    private long _firstBytes;

    public SpeedMonitor(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>Records a sample. <paramref name="timestamp"/> must be monotonic (e.g. Stopwatch based).</summary>
    public void AddSample(TimeSpan timestamp, long cumulativeBytes)
    {
        lock (_lock)
        {
            var ticks = timestamp.Ticks;
            if (_firstTicks < 0)
            {
                _firstTicks = ticks;
                _firstBytes = cumulativeBytes;
            }

            _samples.Enqueue((ticks, cumulativeBytes));
            // Keep one sample older than the window so the window is fully covered.
            while (_samples.Count > 2 && ticks - PeekSecond().Ticks >= _window.Ticks)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>Bytes per second over the rolling window; 0 when not enough data.</summary>
    public double CurrentSpeed
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count < 2)
                {
                    return 0;
                }

                var oldest = _samples.Peek();
                var newest = _samples.Last();
                return Rate(newest.Bytes - oldest.Bytes, newest.Ticks - oldest.Ticks);
            }
        }
    }

    /// <summary>Bytes per second since the first sample.</summary>
    public double AverageSpeed
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0 || _firstTicks < 0)
                {
                    return 0;
                }

                var newest = _samples.Last();
                return Rate(newest.Bytes - _firstBytes, newest.Ticks - _firstTicks);
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _samples.Clear();
            _firstTicks = -1;
            _firstBytes = 0;
        }
    }

    private (long Ticks, long Bytes) PeekSecond()
    {
        using var e = _samples.GetEnumerator();
        e.MoveNext();
        e.MoveNext();
        return e.Current;
    }

    private static double Rate(long bytes, long ticks)
    {
        if (ticks <= 0 || bytes <= 0)
        {
            return 0;
        }

        var value = bytes / TimeSpan.FromTicks(ticks).TotalSeconds;
        return double.IsFinite(value) ? value : 0;
    }
}
