using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OleDownloadManager.Infrastructure.Logging;

/// <summary>Scrubs secrets that might slip into diagnostic text.</summary>
public static partial class LogScrubber
{
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = QueryString().Replace(text, "$1?<redacted>");
        text = UserInfo().Replace(text, "$1<redacted>@");
        text = AuthHeader().Replace(text, "$1: <redacted>");
        return BearerToken().Replace(text, "$1 <redacted>");
    }

    [GeneratedRegex(@"(https?://[^\s?#""']+)\?[^\s#""']*", RegexOptions.IgnoreCase)]
    private static partial Regex QueryString();

    [GeneratedRegex(@"(https?://)[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UserInfo();

    [GeneratedRegex(@"\b(Authorization|Proxy-Authorization|Cookie|Set-Cookie)\s*[:=]\s*[^\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();
}

/// <summary>
/// Minimal rolling file logger: one file per day under the logs folder, written on a background
/// thread, with old files pruned. Messages are scrubbed of URL query strings and credentials.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly int _retainDays;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information, int retainDays = 14)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        _retainDays = retainDays;
        Directory.CreateDirectory(directory);
        PruneOldFiles();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "OleDM log writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.TryAdd(line);
        }
    }

    private void WriteLoop()
    {
        StreamWriter? writer = null;
        var currentDate = DateOnly.MinValue;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (writer is null || today != currentDate)
                {
                    writer?.Dispose();
                    currentDate = today;
                    var path = Path.Combine(_directory, $"oledm-{today:yyyyMMdd}.log");
                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
                }

                writer.WriteLine(line);
                if (_queue.Count == 0)
                {
                    writer.Flush();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Logging must never take the application down.
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retainDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "oledm-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [").Append(LevelName(logLevel)).Append("] ")
                .Append(category).Append(": ")
                .Append(LogScrubber.Scrub(formatter(state, exception)));
            if (exception is not null)
            {
                builder.AppendLine().Append(LogScrubber.Scrub(exception.ToString()));
            }

            provider.Enqueue(builder.ToString());
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
