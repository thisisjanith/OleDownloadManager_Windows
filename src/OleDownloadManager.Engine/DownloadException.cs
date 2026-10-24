using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using OleDownloadManager.Core.Enums;

namespace OleDownloadManager.Engine;

/// <summary>A classified download failure with a concise, user-facing message.</summary>
public sealed class DownloadException : Exception
{
    public DownloadException(DownloadErrorKind kind, string message, bool isTransient, int? httpStatusCode = null,
        TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        IsTransient = isTransient;
        HttpStatusCode = httpStatusCode;
        RetryAfter = retryAfter;
    }

    public DownloadErrorKind Kind { get; }
    public bool IsTransient { get; }
    public int? HttpStatusCode { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Maps exceptions and HTTP status codes to <see cref="DownloadException"/>.</summary>
public static class DownloadErrorClassifier
{
    // Win32 error codes surfaced through IOException.HResult (0x8007xxxx).
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotReady = 21;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorBadNetPath = 53;
    private const int ErrorDevNotExist = 55;
    private const int ErrorUnexpectedNetError = 59;
    private const int ErrorNetNameDeleted = 64;
    private const int ErrorBadNetName = 67;
    private const int ErrorDiskFull = 112;
    private const int ErrorInvalidName = 123;
    private const int ErrorFilenameExcedRange = 206;
    private const int ErrorNoSuchDevice = 433;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorDeviceRemoved = 1617;
    private const int ErrorMediaChanged = 1110;

    public static DownloadException FromStatusCode(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var code = (int)status;
        return code switch
        {
            401 => new DownloadException(DownloadErrorKind.Unauthorized, "The server requires authentication (HTTP 401).", false, code),
            403 => new DownloadException(DownloadErrorKind.Forbidden, "Access was denied by the server (HTTP 403). The link may have expired.", false, code),
            404 => new DownloadException(DownloadErrorKind.NotFound, "The file was not found on the server (HTTP 404).", false, code),
            410 => new DownloadException(DownloadErrorKind.NotFound, "The file is no longer available (HTTP 410).", false, code),
            408 => new DownloadException(DownloadErrorKind.RequestTimeout, "The server timed out waiting for the request (HTTP 408).", true, code, retryAfter),
            429 => new DownloadException(DownloadErrorKind.TooManyRequests, "The server is rate limiting requests (HTTP 429).", true, code, retryAfter),
            416 => new DownloadException(DownloadErrorKind.InvalidRangeResponse, "The server rejected the requested byte range (HTTP 416).", false, code),
            501 or 505 => new DownloadException(DownloadErrorKind.ServerError, $"The server does not support this request (HTTP {code}).", false, code),
            >= 500 and <= 599 => new DownloadException(DownloadErrorKind.ServerError, $"The server reported an error (HTTP {code}).", true, code, retryAfter),
            _ => new DownloadException(DownloadErrorKind.HttpError, $"The server returned HTTP {code}.", false, code),
        };
    }

    /// <summary>Classifies an exception raised while talking to the network.</summary>
    public static DownloadException Classify(Exception exception)
    {
        switch (exception)
        {
            case DownloadException de:
                return de;
            case HttpRequestException hre:
                return FromHttpRequestException(hre);
            case AuthenticationException ae:
                return new DownloadException(DownloadErrorKind.TlsFailure, "A secure connection could not be established. The server's certificate may be invalid.", false, inner: ae);
            case TimeoutException te:
                return new DownloadException(DownloadErrorKind.ConnectionTimeout, "The connection timed out.", true, inner: te);
            case SocketException se:
                return FromSocketError(se.SocketErrorCode, se);
            case IOException io when FindInner<SocketException>(io) is { } se:
                return FromSocketError(se.SocketErrorCode, io);
            case HttpIOException hio:
                return new DownloadException(DownloadErrorKind.IncompleteResponse, "The connection closed before the transfer finished.", true, inner: hio);
            case IOException io when FindInner<AuthenticationException>(io) is not null:
                return new DownloadException(DownloadErrorKind.TlsFailure, "The secure connection failed.", false, inner: io);
            case UnauthorizedAccessException or IOException:
                return ClassifyFileSystem(exception);
            default:
                return new DownloadException(DownloadErrorKind.Unknown, "An unexpected error occurred: " + exception.Message, false, inner: exception);
        }
    }

    /// <summary>Classifies an exception raised by a local file operation.</summary>
    public static DownloadException ClassifyFileSystem(Exception exception)
    {
        if (exception is DownloadException de)
        {
            return de;
        }

        if (exception is UnauthorizedAccessException)
        {
            return new DownloadException(DownloadErrorKind.AccessDenied, "Access to the destination folder was denied.", false, inner: exception);
        }

        if (exception is PathTooLongException)
        {
            return new DownloadException(DownloadErrorKind.PathTooLong, "The destination path is too long.", false, inner: exception);
        }

        if (exception is DirectoryNotFoundException or DriveNotFoundException)
        {
            return new DownloadException(DownloadErrorKind.DestinationUnavailable, "The destination folder is not available. If it is on a removable drive, reconnect it.", false, inner: exception);
        }

        var code = exception.HResult & 0xFFFF;
        return code switch
        {
            ErrorDiskFull or ErrorHandleDiskFull =>
                new DownloadException(DownloadErrorKind.DiskFull, "There is not enough space on the destination drive.", false, inner: exception),
            ErrorAccessDenied =>
                new DownloadException(DownloadErrorKind.AccessDenied, "Access to the destination was denied.", false, inner: exception),
            ErrorSharingViolation or ErrorLockViolation =>
                new DownloadException(DownloadErrorKind.AccessDenied, "The file is in use by another program.", true, inner: exception),
            ErrorNotReady or ErrorDevNotExist or ErrorNoSuchDevice or ErrorDeviceNotConnected or ErrorDeviceRemoved or
            ErrorMediaChanged or ErrorBadNetPath or ErrorBadNetName or ErrorUnexpectedNetError or ErrorNetNameDeleted or
            ErrorPathNotFound or ErrorFileNotFound =>
                new DownloadException(DownloadErrorKind.DestinationUnavailable, "The destination drive is not available. Reconnect it to continue.", false, inner: exception),
            ErrorFilenameExcedRange =>
                new DownloadException(DownloadErrorKind.PathTooLong, "The destination path is too long.", false, inner: exception),
            ErrorInvalidName =>
                new DownloadException(DownloadErrorKind.AccessDenied, "The destination file name is not valid.", false, inner: exception),
            _ => new DownloadException(DownloadErrorKind.Unknown, "A file error occurred: " + exception.Message, false, inner: exception),
        };
    }

    public static bool IsTransient(DownloadErrorKind kind) => kind is
        DownloadErrorKind.ConnectionFailed or DownloadErrorKind.ConnectionTimeout or DownloadErrorKind.ConnectionReset or
        DownloadErrorKind.NetworkUnavailable or DownloadErrorKind.DnsFailure or DownloadErrorKind.RequestTimeout or
        DownloadErrorKind.TooManyRequests or DownloadErrorKind.ServerError or DownloadErrorKind.IncompleteResponse;

    private static DownloadException FromHttpRequestException(HttpRequestException hre)
    {
        if (FindInner<AuthenticationException>(hre) is not null || hre.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return new DownloadException(DownloadErrorKind.TlsFailure, "A secure connection could not be established. The server's certificate may be invalid or untrusted.", false, inner: hre);
        }

        if (FindInner<SocketException>(hre) is { } se)
        {
            return FromSocketError(se.SocketErrorCode, hre);
        }

        return hre.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => new DownloadException(DownloadErrorKind.DnsFailure, "The server name could not be resolved. Check the URL and your connection.", true, inner: hre),
            HttpRequestError.ConnectionError => new DownloadException(DownloadErrorKind.ConnectionFailed, "Could not connect to the server.", true, inner: hre),
            HttpRequestError.ResponseEnded => new DownloadException(DownloadErrorKind.IncompleteResponse, "The server closed the connection before the transfer finished.", true, inner: hre),
            HttpRequestError.ProxyTunnelError => new DownloadException(DownloadErrorKind.ConnectionFailed, "The proxy server refused the connection.", true, inner: hre),
            HttpRequestError.InvalidResponse => new DownloadException(DownloadErrorKind.HttpError, "The server sent an invalid response.", true, inner: hre),
            _ when hre.StatusCode is { } status => FromStatusCode(status),
            _ => new DownloadException(DownloadErrorKind.ConnectionFailed, "A network error occurred: " + hre.Message, true, inner: hre),
        };
    }

    private static DownloadException FromSocketError(SocketError error, Exception inner) => error switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
            new DownloadException(DownloadErrorKind.DnsFailure, "The server name could not be resolved. Check the URL and your connection.", true, inner: inner),
        SocketError.TimedOut =>
            new DownloadException(DownloadErrorKind.ConnectionTimeout, "The connection timed out.", true, inner: inner),
        SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown =>
            new DownloadException(DownloadErrorKind.ConnectionReset, "The connection was reset by the server.", true, inner: inner),
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.NetworkReset =>
            new DownloadException(DownloadErrorKind.NetworkUnavailable, "The network is unavailable.", true, inner: inner),
        SocketError.ConnectionRefused =>
            new DownloadException(DownloadErrorKind.ConnectionFailed, "The server refused the connection.", true, inner: inner),
        _ => new DownloadException(DownloadErrorKind.ConnectionFailed, "A network error occurred.", true, inner: inner),
    };

    private static T? FindInner<T>(Exception exception)
        where T : Exception
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
