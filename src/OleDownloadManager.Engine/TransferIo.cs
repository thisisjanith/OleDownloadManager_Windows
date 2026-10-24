using OleDownloadManager.Core.Enums;

namespace OleDownloadManager.Engine;

/// <summary>Shared low-level I/O helpers for transfer loops.</summary>
internal static class TransferIo
{
    /// <summary>
    /// Writes a chunk to disk. The write is deliberately not cancellable so a chunk is
    /// either fully written (and then counted) or the operation fails with a file error.
    /// </summary>
    public static async ValueTask WriteAsync(FileStream destination, ReadOnlyMemory<byte> data)
    {
        try
        {
            await destination.WriteAsync(data, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw DownloadErrorClassifier.ClassifyFileSystem(ex);
        }
    }

    /// <summary>Flushes buffered data all the way to the storage device.</summary>
    public static void FlushDurably(FileStream stream)
    {
        try
        {
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw DownloadErrorClassifier.ClassifyFileSystem(ex);
        }
    }

    public static FileStream OpenForAppend(string path, long validLength)
    {
        try
        {
            var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 0,
                Options = FileOptions.Asynchronous,
            });

            // Anything past the last durable byte may be garbage from an interrupted write.
            if (stream.Length != validLength)
            {
                stream.SetLength(validLength);
            }

            stream.Seek(validLength, SeekOrigin.Begin);
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw DownloadErrorClassifier.ClassifyFileSystem(ex);
        }
    }
}

/// <summary>
/// Per-connection stall detector. One linked token source is reused for every read (armed before the read,
/// disarmed after), instead of allocating a new source and timer for each chunk.
/// </summary>
internal sealed class ReadWatchdog : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _outer;
    private readonly TimeSpan _timeout;

    public ReadWatchdog(TimeSpan timeout, CancellationToken outer)
    {
        _timeout = timeout;
        _outer = outer;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
    }

    public async ValueTask<int> ReadAsync(Stream source, Memory<byte> buffer)
    {
        _cts.CancelAfter(_timeout);
        try
        {
            var read = await source.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
            // Throttling or disk writes between reads must not count as a stalled connection.
            _cts.CancelAfter(Timeout.InfiniteTimeSpan);
            return read;
        }
        catch (OperationCanceledException ex) when (!_outer.IsCancellationRequested)
        {
            throw new DownloadException(DownloadErrorKind.ConnectionTimeout, "The connection stalled: no data was received for a while.", true, inner: ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DownloadException)
        {
            if (_outer.IsCancellationRequested)
            {
                throw new OperationCanceledException(_outer);
            }

            throw DownloadErrorClassifier.Classify(ex);
        }
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>Thread-safe running totals for a single download.</summary>
internal sealed class TransferCounters
{
    private long _bytes;
    private int _connections;

    public TransferCounters(long initialBytes) => _bytes = initialBytes;

    public long Bytes => Interlocked.Read(ref _bytes);
    public int Connections => Volatile.Read(ref _connections);

    public void Add(long bytes) => Interlocked.Add(ref _bytes, bytes);

    public void Set(long bytes) => Interlocked.Exchange(ref _bytes, bytes);

    public void ConnectionOpened() => Interlocked.Increment(ref _connections);

    public void ConnectionClosed() => Interlocked.Decrement(ref _connections);
}
