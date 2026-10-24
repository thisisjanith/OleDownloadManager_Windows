using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.Engine;

/// <summary>Headers and credentials for one download. Sensitive values live only in memory.</summary>
public sealed class RequestContext
{
    public static readonly RequestContext Empty = new(null, null, null, null);

    public RequestContext(Uri? origin, IReadOnlyDictionary<string, string>? headers, DownloadCredentials? credentials, Uri? referrer)
    {
        Origin = origin;
        Headers = headers ?? new Dictionary<string, string>();
        Credentials = credentials;
        Referrer = referrer;
    }

    /// <summary>The URL the user asked for. Secrets are only sent to this origin.</summary>
    public Uri? Origin { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public DownloadCredentials? Credentials { get; }
    public Uri? Referrer { get; }

    public bool IsSameOrigin(Uri target) =>
        Origin is not null &&
        string.Equals(Origin.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Origin.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        Origin.Port == target.Port;
}

/// <summary>
/// Sends GET requests with deliberate redirect handling: HTTPS-to-HTTP downgrades are refused,
/// the hop count is limited, and credentials/sensitive headers are never forwarded to another origin.
/// </summary>
public sealed class HttpRequestSender
{
    private readonly HttpClient _client;
    private readonly EngineOptions _options;
    private readonly ILogger _logger;

    public HttpRequestSender(HttpClient client, EngineOptions options, ILogger logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    /// <summary>Creates the handler used in production. TLS validation stays enabled.</summary>
    public static SocketsHttpHandler CreateHandler(TimeSpan connectTimeout) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = connectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
        MaxConnectionsPerServer = 32,
        EnableMultipleHttp2Connections = true,
    };

    public static HttpClient CreateClient(HttpMessageHandler handler) => new(handler, disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<HttpResponseMessage> SendAsync(Uri url, RequestContext context, Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; hop <= _options.MaxRedirects; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, current);
            ApplyHeaders(request, context, current);
            configure?.Invoke(request);

            HttpResponseMessage response;
            using (var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                headersTimeout.CancelAfter(_options.ResponseHeadersTimeout);
                try
                {
                    response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new DownloadException(DownloadErrorKind.ConnectionTimeout, "The server did not respond in time.", true, inner: ex);
                }
            }

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new DownloadException(DownloadErrorKind.HttpError, "The server sent a redirect without a destination.", false, (int)response.StatusCode);
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!UrlValidator.IsHttpScheme(next))
            {
                throw new DownloadException(DownloadErrorKind.HttpError, "The server redirected to an unsupported address.", false);
            }

            if (current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
            {
                throw new DownloadException(DownloadErrorKind.TlsFailure, "The server tried to redirect from HTTPS to insecure HTTP, so the download was stopped.", false);
            }

            _logger.LogDebug("Redirect {From} -> {To}", LogRedactor.Url(current), LogRedactor.Url(next));
            current = next;
        }

        throw new DownloadException(DownloadErrorKind.HttpError, "The server redirected too many times.", false);
    }

    private void ApplyHeaders(HttpRequestMessage request, RequestContext context, Uri target)
    {
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        // Byte ranges must refer to the stored representation, so ask for no content coding.
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        if (context.Referrer is { } referrer && UrlValidator.IsHttpScheme(referrer) &&
            !(referrer.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp))
        {
            request.Headers.Referrer = new Uri(referrer.GetLeftPart(UriPartial.Path));
        }

        var sameOrigin = context.IsSameOrigin(target);
        foreach (var (name, value) in context.Headers)
        {
            if (HeaderValidator.IsForbidden(name) || (HeaderValidator.IsSensitive(name) && !sameOrigin))
            {
                continue;
            }

            if (string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.UserAgent.Clear();
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (context.Credentials is { } credentials && sameOrigin)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
