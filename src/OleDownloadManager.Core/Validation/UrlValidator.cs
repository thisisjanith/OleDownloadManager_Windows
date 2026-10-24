namespace OleDownloadManager.Core.Validation;

public static class UrlValidator
{
    public const int MaxUrlLength = 8192;

    /// <summary>
    /// Validates a user- or browser-supplied download URL. Only absolute HTTP and HTTPS URLs
    /// with a host are accepted. URLs carrying embedded credentials are rejected so that
    /// secrets never end up in history or logs.
    /// </summary>
    public static bool TryValidate(string? input, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        var text = input?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            error = "Enter a URL.";
            return false;
        }

        if (text.Length > MaxUrlLength)
        {
            error = "The URL is too long.";
            return false;
        }

        if (text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            error = "The URL contains spaces or control characters.";
            return false;
        }

        // Accept "example.com/file.zip" by assuming HTTPS, but never guess other schemes.
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (text.Contains(':', StringComparison.Ordinal) && !LooksLikeHostWithPort(text))
            {
                error = "Only HTTP and HTTPS URLs are supported.";
                return false;
            }

            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            error = "The URL is not valid.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = $"The \"{parsed.Scheme}\" scheme is not supported. Use HTTP or HTTPS.";
            return false;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            error = "The URL does not contain a host name.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "Do not put credentials in the URL. Use the Authentication fields instead.";
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool IsValid(string? input) => TryValidate(input, out _, out _);

    /// <summary>True for HTTP(S) absolute URIs; used to validate redirect targets.</summary>
    public static bool IsHttpScheme(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool LooksLikeHostWithPort(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var rest = text[(colon + 1)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var port = end < 0 ? rest : rest[..end];
        return port.Length > 0 && port.All(char.IsAsciiDigit);
    }
}
