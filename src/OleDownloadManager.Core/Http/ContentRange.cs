using System.Globalization;

namespace OleDownloadManager.Core.Http;

/// <summary>
/// A parsed "Content-Range: bytes first-last/complete" header (RFC 9110 §14.4).
/// <see cref="First"/>/<see cref="Last"/> are null for the unsatisfied form "bytes */complete";
/// <see cref="CompleteLength"/> is null when the server sends "*".
/// </summary>
public readonly record struct ContentRange(long? First, long? Last, long? CompleteLength)
{
    public long? Length => First is { } f && Last is { } l ? l - f + 1 : null;

    public static bool TryParse(string? value, out ContentRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        const string unit = "bytes ";
        if (!text.StartsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        text = text[unit.Length..].Trim();
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == text.Length - 1)
        {
            return false;
        }

        var rangePart = text[..slash].Trim();
        var completePart = text[(slash + 1)..].Trim();

        long? complete = null;
        if (completePart != "*")
        {
            if (!TryParseNonNegative(completePart, out var c))
            {
                return false;
            }

            complete = c;
        }

        if (rangePart == "*")
        {
            // Unsatisfied-range form requires a known complete length.
            if (complete is null)
            {
                return false;
            }

            range = new ContentRange(null, null, complete);
            return true;
        }

        var dash = rangePart.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash == rangePart.Length - 1)
        {
            return false;
        }

        if (!TryParseNonNegative(rangePart[..dash], out var first) || !TryParseNonNegative(rangePart[(dash + 1)..], out var last))
        {
            return false;
        }

        if (last < first || (complete is { } total && last >= total))
        {
            return false;
        }

        range = new ContentRange(first, last, complete);
        return true;
    }

    /// <summary>True when this range exactly matches the requested inclusive interval.</summary>
    public bool Matches(long first, long last, long? expectedTotal) =>
        First == first && Last == last && (expectedTotal is null || CompleteLength is null || CompleteLength == expectedTotal);

    private static bool TryParseNonNegative(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
}
