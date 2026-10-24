using System.Diagnostics;

namespace OleDownloadManager.Engine;

/// <summary>
/// Token-bucket rate limiter shared by every connection it governs. Callers consume
/// tokens after reading; when the bucket goes into debt they wait until it is repaid,
/// so the aggregate rate converges on the limit regardless of how many connections share it.
/// </summary>
public sealed class BandwidthLimiter
{
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private long _rate;
    private double _tokens;
    private long _lastTimestamp;

    public BandwidthLimiter(long? bytesPerSecond = null, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _lastTimestamp = _time.GetTimestamp();
        SetLimit(bytesPerSecond);
    }

    /// <summary>Current limit in bytes per second, or null for unlimited.</summary>
    public long? Limit
    {
        get
        {
            lock (_lock)
            {
                return _rate > 0 ? _rate : null;
            }
        }
    }

    public void SetLimit(long? bytesPerSecond)
    {
        lock (_lock)
        {
            _rate = bytesPerSecond is > 0 ? bytesPerSecond.Value : 0;
            // Start each new limit with a full (small) burst allowance and no debt.
            _tokens = Capacity;
            _lastTimestamp = _time.GetTimestamp();
        }
    }

    /// <summary>A read size that keeps throttled transfers smooth (about 1/8 s of data).</summary>
    public int PreferredChunkSize(int maximum)
    {
        var rate = Interlocked.Read(ref _rate);
        if (rate <= 0)
        {
            return maximum;
        }

        return (int)Math.Clamp(rate / 8, 1024, maximum);
    }

    private double Capacity => Math.Max(_rate / 4.0, 4096);

    public ValueTask ConsumeAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0)
        {
            return ValueTask.CompletedTask;
        }

        TimeSpan delay;
        lock (_lock)
        {
            if (_rate <= 0)
            {
                return ValueTask.CompletedTask;
            }

            var now = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            _lastTimestamp = now;
            _tokens = Math.Min(Capacity, _tokens + (elapsed * _rate));
            _tokens -= bytes;
            if (_tokens >= 0)
            {
                return ValueTask.CompletedTask;
            }

            delay = TimeSpan.FromSeconds(-_tokens / _rate);
        }

        Debug.Assert(delay > TimeSpan.Zero, "Positive delay expected when in debt.");
        return new ValueTask(Task.Delay(delay, _time, cancellationToken));
    }
}
