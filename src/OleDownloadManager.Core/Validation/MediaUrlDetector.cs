namespace OleDownloadManager.Core.Validation;

/// <summary>
/// Recognizes links to video pages (not files), such as YouTube watch pages and playlists.
/// Downloading those addresses directly only saves the HTML page, so they are routed to the media tool.
/// </summary>
public static class MediaUrlDetector
{
    public static bool IsMediaPage(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && IsMediaPage(uri);

    public static bool IsMediaPage(Uri uri)
    {
        if (!UrlValidator.IsHttpScheme(uri))
        {
            return false;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "m.", "music." })
        {
            if (host.StartsWith(prefix, StringComparison.Ordinal))
            {
                host = host[prefix.Length..];
                break;
            }
        }

        var path = uri.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return host switch
        {
            "youtube.com" or "youtube-nocookie.com" =>
                path.Equals("/watch", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/playlist", StringComparison.OrdinalIgnoreCase) ||
                StartsWithSegment(segments, "shorts", "live", "embed", "v"),
            "youtu.be" => segments.Length == 1,
            "vimeo.com" => segments.Length >= 1 && segments[^1].All(char.IsAsciiDigit),
            "dailymotion.com" => StartsWithSegment(segments, "video", "playlist"),
            "twitch.tv" => StartsWithSegment(segments, "videos") || (segments.Length >= 3 && segments[1] == "clip"),
            "soundcloud.com" => segments.Length >= 2,
            _ => false,
        };
    }

    private static bool StartsWithSegment(string[] segments, params string[] names) =>
        segments.Length >= 2 && names.Contains(segments[0], StringComparer.OrdinalIgnoreCase);
}
