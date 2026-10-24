namespace OleDownloadManager.Core.Validation;

/// <summary>Parses and validates user-supplied custom request headers.</summary>
public static class HeaderValidator
{
    /// <summary>Headers controlled by the engine or the HTTP stack; users cannot override them.</summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade",
        "Range", "If-Range", "TE", "Trailer", "Accept-Encoding", "Expect", "Proxy-Connection",
    };

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
    };

    private static readonly string[] SensitiveFragments = ["token", "auth", "secret", "key", "session", "password", "signature"];

    /// <summary>True when a header's value must be treated as a secret (never logged or stored in plaintext).</summary>
    public static bool IsSensitive(string name) =>
        SensitiveNames.Contains(name) || SensitiveFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

    public static bool IsForbidden(string name) => Forbidden.Contains(name);

    /// <summary>Parses "Name: Value" lines. Blank lines are ignored.</summary>
    public static bool TryParse(string? text, out Dictionary<string, string> headers, out string? error)
    {
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                error = $"Line {lineNumber}: expected \"Name: Value\".";
                return false;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!IsValidToken(name))
            {
                error = $"Line {lineNumber}: \"{name}\" is not a valid header name.";
                return false;
            }

            if (IsForbidden(name))
            {
                error = $"Line {lineNumber}: the {name} header is managed by OleDownloadManager.";
                return false;
            }

            if (value.Any(c => c < 32 && c != '\t'))
            {
                error = $"Line {lineNumber}: the value contains control characters.";
                return false;
            }

            headers[name] = value;
        }

        return true;
    }

    // RFC 9110 token characters.
    private static bool IsValidToken(string name) =>
        name.Length > 0 && name.All(c => c < 128 && (char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)));
}
