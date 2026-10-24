using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.Core.Protocol;

public static class MessageTypes
{
    // Extension -> host
    public const string Hello = "hello";
    public const string GetStatus = "getStatus";
    public const string AddDownload = "addDownload";

    // Host -> extension
    public const string Status = "status";
    public const string DownloadAccepted = "downloadAccepted";
    public const string DownloadRejected = "downloadRejected";
    public const string Error = "error";

    // Second application instance -> running instance (pipe only)
    public const string Activate = "activate";

    public static readonly IReadOnlySet<string> FromExtension = new HashSet<string>(StringComparer.Ordinal) { Hello, GetStatus, AddDownload };
    public static readonly IReadOnlySet<string> FromLocalClient = new HashSet<string>(StringComparer.Ordinal) { Hello, GetStatus, AddDownload, Activate };
}

/// <summary>
/// Message exchanged between the browser extension, the native messaging host and the app.
/// All properties are optional on the wire; <see cref="BrowserMessageValidator"/> enforces the schema.
/// </summary>
public sealed class BrowserMessage
{
    public const int CurrentProtocolVersion = 1;

    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("version")] public int? Version { get; set; }
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("pageUrl")] public string? PageUrl { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("fileSize")] public long? FileSize { get; set; }

    // Responses
    [JsonPropertyName("downloadId")] public string? DownloadId { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("appVersion")] public string? AppVersion { get; set; }
    [JsonPropertyName("appRunning")] public bool? AppRunning { get; set; }
    [JsonPropertyName("activeDownloads")] public int? ActiveDownloads { get; set; }

    public static BrowserMessage Reply(string type, BrowserMessage? request = null) => new()
    {
        Type = type,
        Version = CurrentProtocolVersion,
        RequestId = request?.RequestId,
    };

    public static BrowserMessage ErrorReply(string reason, BrowserMessage? request = null)
    {
        var message = Reply(MessageTypes.Error, request);
        message.Reason = reason;
        return message;
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 8)]
[JsonSerializable(typeof(BrowserMessage))]
public sealed partial class BrowserMessageJsonContext : JsonSerializerContext;

/// <summary>Validates untrusted messages before any action is taken.</summary>
public static class BrowserMessageValidator
{
    public const int MaxMessageBytes = 64 * 1024;
    private const int MaxRequestIdLength = 64;
    private const int MaxFileNameLength = 1024;

    public static bool TryParse(ReadOnlySpan<byte> utf8Json, IReadOnlySet<string> allowedTypes, out BrowserMessage? message, out string? error)
    {
        message = null;
        if (utf8Json.Length == 0 || utf8Json.Length > MaxMessageBytes)
        {
            error = "Message size is invalid.";
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize(utf8Json, BrowserMessageJsonContext.Default.BrowserMessage);
        }
        catch (JsonException)
        {
            error = "Message is not valid JSON.";
            return false;
        }

        if (message is null)
        {
            error = "Message is empty.";
            return false;
        }

        return Validate(message, allowedTypes, out error);
    }

    public static bool Validate(BrowserMessage message, IReadOnlySet<string> allowedTypes, out string? error)
    {
        error = null;
        if (message.Type is null || !allowedTypes.Contains(message.Type))
        {
            error = "Unsupported message type.";
            return false;
        }

        if (message.Version is not { } version || version < 1 || version > BrowserMessage.CurrentProtocolVersion)
        {
            error = $"Unsupported protocol version. This host supports version {BrowserMessage.CurrentProtocolVersion}.";
            return false;
        }

        if (message.RequestId is { } requestId &&
            (requestId.Length > MaxRequestIdLength || !requestId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            error = "Invalid request id.";
            return false;
        }

        if (message.Type == MessageTypes.AddDownload)
        {
            if (!UrlValidator.TryValidate(message.Url, out var uri, out var urlError) || !message.Url!.Contains("://", StringComparison.Ordinal))
            {
                error = urlError ?? "Invalid URL.";
                return false;
            }

            message.Url = uri!.AbsoluteUri;

            if (message.PageUrl is { Length: > 0 } pageUrl)
            {
                if (pageUrl.Length > UrlValidator.MaxUrlLength || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || !UrlValidator.IsHttpScheme(page))
                {
                    // The page URL is informational only; drop it rather than reject the download.
                    message.PageUrl = null;
                }
            }

            if (message.FileName is { } fileName)
            {
                message.FileName = fileName.Length > MaxFileNameLength ? null : FileNameSanitizer.Sanitize(fileName);
            }

            if (message.FileSize is < 0)
            {
                message.FileSize = null;
            }
        }

        return true;
    }

    public static byte[] Serialize(BrowserMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, BrowserMessageJsonContext.Default.BrowserMessage);
}

/// <summary>
/// Native messaging framing: a 32-bit little-endian length followed by UTF-8 JSON.
/// Also used on the local named pipe between the host and the application.
/// </summary>
public static class MessageFraming
{
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxBytes)
        {
            throw new InvalidDataException($"Message length {length} is outside the allowed range.");
        }

        var buffer = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, buffer, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("The stream ended in the middle of a message.");
        }

        return buffer;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Task WriteAsync(Stream stream, BrowserMessage message, CancellationToken cancellationToken) =>
        WriteAsync(stream, BrowserMessageValidator.Serialize(message), cancellationToken);

    /// <summary>Returns false if the stream ended cleanly before the first byte.</summary>
    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The stream ended in the middle of a message.");
            }

            read += n;
        }

        return true;
    }
}
