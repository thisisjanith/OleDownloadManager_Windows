using System.Text;

namespace OleDownloadManager.Core.Validation;

/// <summary>
/// Produces log-safe representations of URLs. Query values, fragments and user info are
/// removed because they frequently carry tokens or signatures.
/// </summary>
public static class LogRedactor
{
    public static string Url(Uri? uri)
    {
        if (uri is null)
        {
            return "(none)";
        }

        if (!uri.IsAbsoluteUri)
        {
            return "(relative)";
        }

        var builder = new StringBuilder();
        builder.Append(uri.Scheme).Append("://").Append(uri.Host);
        if (!uri.IsDefaultPort)
        {
            builder.Append(':').Append(uri.Port);
        }

        builder.Append(uri.AbsolutePath);
        if (uri.Query.Length > 1)
        {
            var names = uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2)[0]);
            builder.Append('?').Append(string.Join("&", names.Select(n => n + "=***")));
        }

        return builder.ToString();
    }

    public static string Url(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Url(uri) : "(invalid url)";
}
