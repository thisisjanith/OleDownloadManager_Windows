using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Http;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.Engine;

/// <summary>
/// Outcome of probing a URL. When the server ignored the probe's Range header and started
/// sending the full body, <see cref="FullResponse"/> holds that response so it can be used
/// as the single-connection download instead of requesting the file twice.
/// </summary>
public sealed class ProbeOutcome : IDisposable
{
    public required ProbeResult Result { get; init; }

    /// <summary>A strong ETag usable with If-Range, or null.</summary>
    public string? StrongETag { get; init; }

    public HttpResponseMessage? FullResponse { get; set; }

    public void Dispose() => FullResponse?.Dispose();
}

/// <summary>Probes servers and performs single-connection (non-range) transfers.</summary>
public sealed class HttpDownloadEngine
{
    private static readonly Dictionary<string, string> ExtensionsByMediaType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ".pdf",
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/x-7z-compressed"] = ".7z",
        ["application/gzip"] = ".gz",
        ["application/x-msdownload"] = ".exe",
        ["application/x-msi"] = ".msi",
        ["application/json"] = ".json",
        ["text/plain"] = ".txt",
        ["text/html"] = ".html",
        ["text/csv"] = ".csv",
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["audio/mpeg"] = ".mp3",
        ["audio/wav"] = ".wav",
        ["audio/flac"] = ".flac",
    };

    private readonly HttpRequestSender _sender;
    private readonly EngineOptions _options;
    private readonly ILogger _logger;

    public HttpDownloadEngine(HttpRequestSender sender, EngineOptions options, ILogger logger)
    {
        _sender = sender;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Issues "Range: bytes=0-0" and inspects the reply. Range support is only reported when the
    /// server actually answers 206 with a matching, well-formed Content-Range.
    /// </summary>
    public async Task<ProbeOutcome> ProbeAsync(Uri url, RequestContext context, CancellationToken cancellationToken)
    {
        var response = await _sender.SendAsync(url, context, r => r.Headers.Range = new RangeHeaderValue(0, 0), cancellationToken).ConfigureAwait(false);
        var keepResponse = false;
        try
        {
            var finalUrl = response.RequestMessage?.RequestUri ?? url;
            var status = response.StatusCode;
            var etag = response.Headers.ETag;
            var lastModified = response.Content.Headers.LastModified;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var fileName = SuggestFileName(response, finalUrl, url);
            long? length = null;
            var supportsRanges = false;

            if (status == HttpStatusCode.PartialContent)
            {
                if (ContentRange.TryParse(GetRawHeader(response, "Content-Range"), out var range) &&
                    range.First == 0 && range.Last == 0 && range.CompleteLength is { } total)
                {
                    supportsRanges = true;
                    length = total;
                }
                else
                {
                    _logger.LogInformation("Server returned an inconsistent Content-Range for {Url}; ranges disabled", LogRedactor.Url(finalUrl));
                }
            }
            else if (status == HttpStatusCode.OK)
            {
                length = GetIdentityContentLength(response);
                keepResponse = true;
            }
            else if (status == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // An empty resource cannot satisfy bytes=0-0.
                if (ContentRange.TryParse(GetRawHeader(response, "Content-Range"), out var range) && range.CompleteLength == 0)
                {
                    length = 0;
                    supportsRanges = true;
                }
            }
            else
            {
                throw DownloadErrorClassifier.FromStatusCode(status, RetryPolicy.ParseRetryAfter(response, DateTimeOffset.UtcNow));
            }

            var result = new ProbeResult
            {
                FinalUrl = finalUrl,
                SuggestedFileName = AddExtensionFromMediaType(fileName, mediaType),
                ContentLength = length,
                SupportsRanges = supportsRanges,
                ETag = etag?.ToString(),
                LastModified = lastModified,
                ContentType = mediaType,
                StatusCode = (int)status,
            };

            return new ProbeOutcome
            {
                Result = result,
                StrongETag = etag is { IsWeak: false } ? etag.ToString() : null,
                FullResponse = keepResponse ? response : null,
            };
        }
        finally
        {
            if (!keepResponse)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>Requests the whole resource without a Range header.</summary>
    public async Task<HttpResponseMessage> GetFullAsync(Uri url, RequestContext context, CancellationToken cancellationToken)
    {
        var response = await _sender.SendAsync(url, context, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var error = DownloadErrorClassifier.FromStatusCode(response.StatusCode, RetryPolicy.ParseRetryAfter(response, DateTimeOffset.UtcNow));
            response.Dispose();
            throw error;
        }

        return response;
    }

    /// <summary>
    /// Streams a full (200) response into <paramref name="filePath"/>, starting from an empty file.
    /// Content codings such as gzip are decoded, in which case the final size is unknown up front.
    /// </summary>
    internal async Task<long> DownloadSingleAsync(HttpResponseMessage response, string filePath, TransferCounters counters,
        IReadOnlyList<BandwidthLimiter> limiters, CancellationToken cancellationToken)
    {
        var expected = GetIdentityContentLength(response);
        var network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var source = WrapContentDecoding(response, network);
        await using var file = TransferIo.OpenForAppend(filePath, 0);
        counters.Set(0);
        counters.ConnectionOpened();
        var buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        using var watchdog = new ReadWatchdog(_options.ReadInactivityTimeout, cancellationToken);
        long written = 0;
        try
        {
            while (true)
            {
                var chunk = limiters.Count == 0 ? _options.BufferSize : limiters.Min(l => l.PreferredChunkSize(_options.BufferSize));
                var read = await watchdog.ReadAsync(source, buffer.AsMemory(0, chunk)).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (expected is { } max && written + read > max)
                {
                    throw new DownloadException(DownloadErrorKind.HttpError, "The server sent more data than it announced.", true);
                }

                await TransferIo.WriteAsync(file, buffer.AsMemory(0, read)).ConfigureAwait(false);
                written += read;
                counters.Add(read);
                foreach (var limiter in limiters)
                {
                    await limiter.ConsumeAsync(read, cancellationToken).ConfigureAwait(false);
                }
            }

            TransferIo.FlushDurably(file);
        }
        finally
        {
            counters.ConnectionClosed();
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (expected is { } total && written != total)
        {
            throw new DownloadException(DownloadErrorKind.IncompleteResponse, $"The transfer ended early ({written:N0} of {total:N0} bytes).", true);
        }

        return written;
    }

    /// <summary>Content-Length when the body is not content-coded; otherwise null (size unknown).</summary>
    internal static long? GetIdentityContentLength(HttpResponseMessage response)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count > 0 && encodings.Any(e => !string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return response.Content.Headers.ContentLength;
    }

    private static Stream WrapContentDecoding(HttpResponseMessage response, Stream stream)
    {
        var encoding = response.Content.Headers.ContentEncoding.LastOrDefault()?.ToLowerInvariant();
        return encoding switch
        {
            "gzip" or "x-gzip" => new GZipStream(stream, CompressionMode.Decompress),
            "deflate" => new ZLibStream(stream, CompressionMode.Decompress),
            "br" => new BrotliStream(stream, CompressionMode.Decompress),
            null or "identity" => stream,
            _ => throw new DownloadException(DownloadErrorKind.HttpError, $"The server used an unsupported content encoding ({encoding}).", false),
        };
    }

    internal static string? GetRawHeader(HttpResponseMessage response, string name)
    {
        if (response.Content.Headers.NonValidated.TryGetValues(name, out var contentValues))
        {
            return contentValues.FirstOrDefault();
        }

        return response.Headers.NonValidated.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }

    /// <summary>Content-Disposition (filename* preferred) or the last URL path segment, sanitized.</summary>
    internal static string SuggestFileName(HttpResponseMessage response, Uri finalUrl, Uri originalUrl)
    {
        var fromHeader = ParseContentDispositionFileName(GetRawHeader(response, "Content-Disposition"));
        if (!string.IsNullOrWhiteSpace(fromHeader))
        {
            return FileNameSanitizer.Sanitize(fromHeader);
        }

        var fromFinal = FileNameSanitizer.FromUrl(finalUrl);
        if (fromFinal != FileNameSanitizer.DefaultFileName && Path.HasExtension(fromFinal))
        {
            return fromFinal;
        }

        var fromOriginal = FileNameSanitizer.FromUrl(originalUrl);
        return Path.HasExtension(fromOriginal) ? fromOriginal : fromFinal;
    }

    /// <summary>Parses a Content-Disposition value, tolerating raw UTF-8 and unquoted names.</summary>
    internal static string? ParseContentDispositionFileName(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        if (ContentDispositionHeaderValue.TryParse(header, out var parsed))
        {
            if (!string.IsNullOrWhiteSpace(parsed.FileNameStar))
            {
                return parsed.FileNameStar;
            }

            if (!string.IsNullOrWhiteSpace(parsed.FileName))
            {
                return Unquote(parsed.FileName);
            }
        }

        // Fallback for headers .NET rejects (e.g. unescaped non-ASCII).
        foreach (var part in header.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("filename*=", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["filename*=".Length..];
                var quote = value.IndexOf("''", StringComparison.Ordinal);
                if (quote >= 0)
                {
                    try
                    {
                        return Uri.UnescapeDataString(value[(quote + 2)..]);
                    }
                    catch (UriFormatException)
                    {
                        return null;
                    }
                }
            }
        }

        foreach (var part in header.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
            {
                return Unquote(trimmed["filename=".Length..]);
            }
        }

        return null;
    }

    internal static string AddExtensionFromMediaType(string fileName, string? mediaType)
    {
        if (Path.HasExtension(fileName) || mediaType is null || !ExtensionsByMediaType.TryGetValue(mediaType, out var extension))
        {
            return fileName;
        }

        return fileName + extension;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        return trimmed;
    }
}
