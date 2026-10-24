using System.Globalization;
using System.Text;

namespace OleDownloadManager.Core.Validation;

/// <summary>
/// Turns untrusted names (from URLs, Content-Disposition headers, browsers or users) into a
/// single, safe Windows file name. The result never contains directory separators, so it
/// cannot escape the destination directory.
/// </summary>
public static class FileNameSanitizer
{
    public const string DefaultFileName = "download";

    /// <summary>Upper bound for a single name component, leaving room for long directory paths.</summary>
    public const int MaxFileNameLength = 180;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
        "CONIN$", "CONOUT$",
    };

    public static string Sanitize(string? name, string fallback = DefaultFileName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        // Keep only the last path component; servers may send "../../x" or "C:\x".
        var text = name.Replace('\\', '/');
        var slash = text.LastIndexOf('/');
        if (slash >= 0)
        {
            text = text[(slash + 1)..];
        }

        text = text.Normalize(NormalizationForm.FormC);

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < 32 || c == 127 || IsInvalidWindowsChar(c))
            {
                builder.Append('_');
            }
            else if (IsBidiOrInvisibleFormatChar(c))
            {
                // Drop direction overrides that can disguise extensions (e.g. "txt.exe" shown as "exe.txt").
            }
            else
            {
                builder.Append(c);
            }
        }

        text = builder.ToString().Trim().TrimEnd('.', ' ');
        text = text.TrimStart(' ');

        if (text.Length == 0 || text.All(ch => ch == '.' || ch == '_'))
        {
            return fallback;
        }

        var stem = text;
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            stem = text[..dot];
        }

        if (ReservedNames.Contains(stem.TrimEnd(' ')))
        {
            text = "_" + text;
        }

        return Truncate(text, MaxFileNameLength);
    }

    /// <summary>Shortens a name while preserving its extension and not splitting surrogate pairs.</summary>
    public static string Truncate(string name, int maxLength)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        if (extension.Length > 20)
        {
            extension = "";
        }

        var stemLength = Math.Max(1, maxLength - extension.Length);
        var stem = name[..^extension.Length];
        var builder = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(stem);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (builder.Length + element.Length > stemLength)
            {
                break;
            }

            builder.Append(element);
        }

        return builder.ToString().TrimEnd('.', ' ') + extension;
    }

    /// <summary>Derives a file name from the last non-empty path segment of a URL.</summary>
    public static string FromUrl(Uri url)
    {
        var path = url.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return Sanitize(url.Host.Replace('.', '_'));
        }

        string last;
        try
        {
            last = Uri.UnescapeDataString(segments[^1]);
        }
        catch (UriFormatException)
        {
            last = segments[^1];
        }

        return Sanitize(last);
    }

    /// <summary>Returns "name (n).ext" for conflict renaming.</summary>
    public static string WithCounter(string fileName, int counter)
    {
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        // Keep compound extensions such as ".tar.gz" together.
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".tar" + extension;
            stem = stem[..^4];
        }

        return $"{stem} ({counter}){extension}";
    }

    private static bool IsInvalidWindowsChar(char c) => c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    private static bool IsBidiOrInvisibleFormatChar(char c) =>
        c is '\u200E' or '\u200F' or '\u061C' or '\uFEFF' or '\u200B' or '\u2060'
        || (c >= '\u202A' && c <= '\u202E')
        || (c >= '\u2066' && c <= '\u2069');
}
