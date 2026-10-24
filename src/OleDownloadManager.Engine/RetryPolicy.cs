namespace OleDownloadManager.Engine;

/// <summary>Exponential backoff with jitter, honouring server Retry-After hints.</summary>
public sealed class RetryPolicy
{
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    public RetryPolicy(int maxAttempts, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null, Func<double>? jitterSource = null)
    {
        MaxAttempts = Math.Max(0, maxAttempts);
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(60);
        _jitter = jitterSource ?? Random.Shared.NextDouble;
    }

    private readonly Func<double> _jitter;

    public int MaxAttempts { get; }
    public TimeSpan BaseDelay { get; }
    public TimeSpan MaxDelay { get; }

    /// <summary>True when another attempt is allowed after <paramref name="failedAttempts"/> failures.</summary>
    public bool CanRetry(int failedAttempts, DownloadException error) => error.IsTransient && failedAttempts <= MaxAttempts;

    /// <summary>Delay before retry number <paramref name="attempt"/> (1-based).</summary>
    public TimeSpan GetDelay(int attempt, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } hint && hint > TimeSpan.Zero)
        {
            return hint > MaxRetryAfter ? MaxRetryAfter : hint;
        }

        var exponent = Math.Clamp(attempt - 1, 0, 16);
        var raw = BaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(raw, MaxDelay.TotalMilliseconds);
        // +/-20% jitter avoids synchronized retries across segments.
        var factor = 0.8 + (0.4 * Math.Clamp(_jitter(), 0, 1));
        return TimeSpan.FromMilliseconds(Math.Min(capped * factor, MaxDelay.TotalMilliseconds));
    }

    /// <summary>Reads Retry-After as either delta-seconds or an HTTP date.</summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header.Date is { } date)
        {
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
