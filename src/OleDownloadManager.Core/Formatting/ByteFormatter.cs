using System.Globalization;

namespace OleDownloadManager.Core.Formatting;

/// <summary>Human-readable sizes, speeds and durations (binary units, displayed as KB/MB/GB like Windows Explorer).</summary>
public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{bytes} B");
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string FormatSize(long? bytes) => bytes is null ? "Unknown" : FormatSize(bytes.Value);

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0)
        {
            return "";
        }

        if (bytesPerSecond < 1024 * 1024)
        {
            return (bytesPerSecond / 1024).ToString(bytesPerSecond < 10 * 1024 ? "0.0" : "0", CultureInfo.CurrentCulture) + " KB/s";
        }

        var mb = bytesPerSecond / (1024 * 1024);
        return mb.ToString(mb >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " MB/s";
    }

    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } d || d < TimeSpan.Zero)
        {
            return "";
        }

        if (d.TotalSeconds < 60)
        {
            return $"{Math.Max(1, (int)d.TotalSeconds)}s";
        }

        if (d.TotalMinutes < 60)
        {
            return $"{(int)d.TotalMinutes}m {d.Seconds:D2}s";
        }

        if (d.TotalHours < 24)
        {
            return $"{(int)d.TotalHours}h {d.Minutes:D2}m";
        }

        return $"{(int)d.TotalDays}d {d.Hours}h";
    }
}
