namespace OleDownloadManager.Core.Formatting;

/// <summary>Progress calculations that are safe against unknown sizes, zero and invalid values.</summary>
public static class ProgressMath
{
    /// <summary>Fraction in [0,1], or null when the total size is unknown.</summary>
    public static double? Fraction(long downloaded, long? total, bool completed = false)
    {
        if (total is null || total < 0)
        {
            return completed ? 1 : null;
        }

        if (total == 0)
        {
            return completed ? 1 : 0;
        }

        var value = (double)Math.Max(0, downloaded) / total.Value;
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : null;
    }

    /// <summary>Remaining time, or null when it cannot be calculated meaningfully.</summary>
    public static TimeSpan? EstimateRemaining(long downloaded, long? total, double bytesPerSecond)
    {
        if (total is null || total < 0 || !double.IsFinite(bytesPerSecond) || bytesPerSecond < 1)
        {
            return null;
        }

        var remaining = Math.Max(0, total.Value - Math.Max(0, downloaded));
        var seconds = remaining / bytesPerSecond;
        // Cap at ~100 days to avoid TimeSpan overflow and absurd displays.
        if (!double.IsFinite(seconds) || seconds > 8_640_000)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Ceiling(seconds));
    }
}
