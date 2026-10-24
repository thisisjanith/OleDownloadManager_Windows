using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Http;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.Engine;

/// <summary>Inputs for one segmented transfer run.</summary>
internal sealed class SegmentRun
{
    public required DownloadItem Item { get; init; }
    public required Uri Url { get; init; }
    public required RequestContext Context { get; init; }
    public required string TemporaryDirectory { get; init; }
    public required long TotalSize { get; init; }
    public required int MaxConnections { get; init; }
    public required TransferCounters Counters { get; init; }
    public required IReadOnlyList<BandwidthLimiter> Limiters { get; init; }

    /// <summary>Strong ETag recorded for the representation being assembled.</summary>
    public string? StrongETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
}

/// <summary>
/// Downloads a file as several independent byte ranges in parallel. Each segment has its own
/// temporary file, every 206 response is validated against the requested range, and a changed
/// source representation aborts the run instead of mixing data.
/// </summary>
internal sealed class SegmentedDownloadEngine
{
    /// <summary>Upper bound on segment files per download, including dynamic splits.</summary>
    private const int MaxSegmentCount = 64;

    private readonly HttpRequestSender _sender;
    private readonly EngineOptions _options;
    private readonly ILogger _logger;

    public SegmentedDownloadEngine(HttpRequestSender sender, EngineOptions options, ILogger logger)
    {
        _sender = sender;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(SegmentRun run, CancellationToken cancellationToken)
    {
        var state = new RunState(Math.Clamp(run.MaxConnections, 1, SegmentCalculator.MaxConnections));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? fatal = null;

        async Task WorkerAsync()
        {
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    var segment = TakeWork(run, state);
                    if (segment is null)
                    {
                        return;
                    }

                    try
                    {
                        await RunSegmentWithRetriesAsync(run, state, segment, linked.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Release(run, state, segment);
                    }
                }
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
            {
                // Pause, cancel or a sibling failure; partial data has been flushed.
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref fatal, ex, null);
                await linked.CancelAsync().ConfigureAwait(false);
            }
        }

        var workers = Enumerable.Range(0, state.AllowedWorkers).Select(_ => Task.Run(WorkerAsync, CancellationToken.None)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (fatal is not null)
        {
            ExceptionDispatchInfo.Throw(fatal);
        }

        lock (run.Item.SyncRoot)
        {
            if (run.Item.Segments.Any(s => !s.IsComplete))
            {
                throw new DownloadException(DownloadErrorKind.IncompleteResponse, "Some parts of the file could not be downloaded.", true);
            }
        }
    }

    private DownloadSegment? TakeWork(SegmentRun run, RunState state)
    {
        lock (run.Item.SyncRoot)
        {
            if (state.ActiveWorkers >= state.AllowedWorkers)
            {
                return null;
            }

            var pending = run.Item.Segments
                .Where(s => !s.IsComplete && !state.InProgress.Contains(s.Index))
                .OrderBy(s => s.StartOffset)
                .FirstOrDefault();

            if (pending is null && _options.DynamicSegmentSplitting && run.Item.Segments.Count < MaxSegmentCount)
            {
                pending = TrySplitLargest(run, state);
            }

            if (pending is null)
            {
                return null;
            }

            state.InProgress.Add(pending.Index);
            state.ActiveWorkers++;
            pending.State = SegmentState.Downloading;
            return pending;
        }
    }

    /// <summary>
    /// Gives an idle connection the second half of the largest in-flight segment. The split point
    /// stays at least two buffers ahead of the active writer, so no in-flight chunk can cross it.
    /// Caller holds the item lock.
    /// </summary>
    private DownloadSegment? TrySplitLargest(SegmentRun run, RunState state)
    {
        var candidate = run.Item.Segments
            .Where(s => state.InProgress.Contains(s.Index) && !s.IsComplete)
            .OrderByDescending(s => s.Remaining)
            .FirstOrDefault();
        if (candidate is null)
        {
            return null;
        }

        var minimum = Math.Max(_options.MinimumSegmentSize, 4L * _options.BufferSize);
        var split = SegmentCalculator.TrySplitRemaining(candidate.StartOffset, candidate.EndOffset, candidate.DownloadedBytes, minimum);
        if (split is not { } range)
        {
            return null;
        }

        var nextByte = candidate.StartOffset + candidate.DownloadedBytes;
        if (range.Start < nextByte + (2L * _options.BufferSize))
        {
            return null;
        }

        var segment = new DownloadSegment
        {
            Index = run.Item.Segments.Max(s => s.Index) + 1,
            StartOffset = range.Start,
            EndOffset = range.End,
        };
        candidate.EndOffset = range.Start - 1;
        run.Item.Segments.Add(segment);
        run.Item.Segments.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));

        // A stale file from an earlier, unpersisted split may exist with this index.
        TryDelete(Path.Combine(run.TemporaryDirectory, segment.TempFileName));
        _logger.LogDebug("Split segment {Index} at {Offset} into new segment {NewIndex}", candidate.Index, range.Start, segment.Index);
        return segment;
    }

    private static void Release(SegmentRun run, RunState state, DownloadSegment segment)
    {
        lock (run.Item.SyncRoot)
        {
            state.InProgress.Remove(segment.Index);
            state.ActiveWorkers--;
            segment.State = segment.IsComplete ? SegmentState.Completed : SegmentState.Pending;
        }
    }

    private async Task RunSegmentWithRetriesAsync(SegmentRun run, RunState state, DownloadSegment segment, CancellationToken cancellationToken)
    {
        var retry = new RetryPolicy(_options.SegmentRetries, _options.RetryBaseDelay);
        var failures = 0;
        while (true)
        {
            lock (run.Item.SyncRoot)
            {
                if (segment.IsComplete)
                {
                    return;
                }
            }

            try
            {
                await DownloadRangeAsync(run, segment, cancellationToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (DownloadException ex) when (ex.IsTransient && !cancellationToken.IsCancellationRequested)
            {
                // A connection that made progress before failing resets the failure streak.
                failures = ex.Data["received"] is long received && received > 0 ? 1 : failures + 1;
                lock (run.Item.SyncRoot)
                {
                    segment.RetryCount++;
                    segment.LastError = ex.Message;
                }

                var throttled = ex.Kind == DownloadErrorKind.TooManyRequests || ex.HttpStatusCode == 503;
                if (throttled && state.ReduceConcurrency())
                {
                    _logger.LogInformation("Server throttled parallel connections; reducing to {Count}", state.AllowedWorkers);
                    lock (run.Item.SyncRoot)
                    {
                        if (state.ActiveWorkers > state.AllowedWorkers)
                        {
                            // Hand the segment back; a remaining connection will pick it up.
                            return;
                        }
                    }
                }

                if (!retry.CanRetry(failures, ex))
                {
                    throw;
                }

                var delay = retry.GetDelay(failures, ex.RetryAfter);
                _logger.LogDebug("Segment {Index} retry {Attempt} in {Delay}: {Error}", segment.Index, failures, delay, ex.Message);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Requests the segment's remaining range and appends it to the segment file.</summary>
    private async Task<long> DownloadRangeAsync(SegmentRun run, DownloadSegment segment, CancellationToken cancellationToken)
    {
        long from;
        long to;
        long validLength;
        lock (run.Item.SyncRoot)
        {
            from = segment.StartOffset + segment.DownloadedBytes;
            to = segment.EndOffset;
            validLength = segment.DownloadedBytes;
        }

        if (from > to)
        {
            return 0;
        }

        var path = Path.Combine(run.TemporaryDirectory, segment.TempFileName);
        var sentIfRange = false;
        using var response = await _sender.SendAsync(run.Url, run.Context, request =>
        {
            request.Headers.Range = new RangeHeaderValue(from, to);
            if (run.StrongETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(tag);
                sentIfRange = true;
            }
            else if (run.LastModified is { } lastModified)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
                sentIfRange = true;
            }
        }, cancellationToken).ConfigureAwait(false);

        var servedLast = ValidateRangeResponse(run, response, from, to, sentIfRange);

        long received = 0;
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var file = TransferIo.OpenForAppend(path, validLength);
        var buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        var sinceFlush = Stopwatch.StartNew();
        using var watchdog = new ReadWatchdog(_options.ReadInactivityTimeout, cancellationToken);
        var fileFailed = false;
        run.Counters.ConnectionOpened();
        try
        {
            while (true)
            {
                long allowed;
                lock (run.Item.SyncRoot)
                {
                    allowed = Math.Min(segment.EndOffset, servedLast) - (segment.StartOffset + segment.DownloadedBytes) + 1;
                }

                if (allowed <= 0)
                {
                    break;
                }

                var chunk = (int)Math.Min(allowed, PreferredChunk(run));
                var read = await watchdog.ReadAsync(stream, buffer.AsMemory(0, chunk)).ConfigureAwait(false);
                if (read == 0)
                {
                    var error = new DownloadException(DownloadErrorKind.IncompleteResponse, "The server closed a connection early.", true);
                    error.Data["received"] = received;
                    throw error;
                }

                lock (run.Item.SyncRoot)
                {
                    // The segment may have been split while reading; never write past its end.
                    var stillAllowed = segment.EndOffset - (segment.StartOffset + segment.DownloadedBytes) + 1;
                    read = (int)Math.Min(read, Math.Max(0, stillAllowed));
                }

                if (read == 0)
                {
                    break;
                }

                try
                {
                    await TransferIo.WriteAsync(file, buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
                catch
                {
                    fileFailed = true;
                    throw;
                }

                lock (run.Item.SyncRoot)
                {
                    segment.DownloadedBytes += read;
                }

                received += read;
                run.Counters.Add(read);
                foreach (var limiter in run.Limiters)
                {
                    await limiter.ConsumeAsync(read, cancellationToken).ConfigureAwait(false);
                }

                if (sinceFlush.Elapsed >= _options.CheckpointInterval)
                {
                    FlushAndMarkDurable(run, segment, file);
                    sinceFlush.Restart();
                }
            }
        }
        catch (DownloadException ex) when (ex.IsTransient)
        {
            ex.Data["received"] = received;
            throw;
        }
        finally
        {
            run.Counters.ConnectionClosed();
            ArrayPool<byte>.Shared.Return(buffer);
            if (!fileFailed)
            {
                try
                {
                    FlushAndMarkDurable(run, segment, file);
                }
                catch (DownloadException ex)
                {
                    _logger.LogWarning("Could not flush segment {Index}: {Error}", segment.Index, ex.Message);
                }
            }

            await file.DisposeAsync().ConfigureAwait(false);
        }

        return received;
    }

    private int PreferredChunk(SegmentRun run)
    {
        var chunk = _options.BufferSize;
        foreach (var limiter in run.Limiters)
        {
            chunk = Math.Min(chunk, limiter.PreferredChunkSize(_options.BufferSize));
        }

        return chunk;
    }

    private static void FlushAndMarkDurable(SegmentRun run, DownloadSegment segment, FileStream file)
    {
        long written;
        lock (run.Item.SyncRoot)
        {
            written = segment.DownloadedBytes;
        }

        TransferIo.FlushDurably(file);
        lock (run.Item.SyncRoot)
        {
            segment.DurableBytes = written;
        }
    }

    /// <summary>
    /// Ensures a 206 response starts at the requested byte, stays within the request and comes from the
    /// same representation. Returns the last byte offset the server will send.
    /// </summary>
    private long ValidateRangeResponse(SegmentRun run, HttpResponseMessage response, long from, long to, bool sentIfRange)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            if (sentIfRange)
            {
                throw new DownloadException(DownloadErrorKind.SourceChanged, "The file on the server has changed since the download started.", false);
            }

            throw new DownloadException(DownloadErrorKind.InvalidRangeResponse, "The server stopped honouring byte-range requests.", false);
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw DownloadErrorClassifier.FromStatusCode(response.StatusCode, RetryPolicy.ParseRetryAfter(response, DateTimeOffset.UtcNow));
        }

        if (!ContentRange.TryParse(HttpDownloadEngine.GetRawHeader(response, "Content-Range"), out var range) ||
            range.First != from || range.Last is not { } last || last > to ||
            (range.CompleteLength is { } total && total != run.TotalSize))
        {
            _logger.LogWarning("Invalid Content-Range for {Url}: requested {From}-{To}", LogRedactor.Url(run.Url), from, to);
            if (range.CompleteLength is { } changedTotal && changedTotal != run.TotalSize)
            {
                throw new DownloadException(DownloadErrorKind.SourceChanged, "The file size on the server changed during the download.", false);
            }

            throw new DownloadException(DownloadErrorKind.InvalidRangeResponse, "The server returned a byte range that does not match the request.", false);
        }

        if (run.StrongETag is { } expected && response.Headers.ETag is { IsWeak: false } actual && actual.ToString() != expected)
        {
            throw new DownloadException(DownloadErrorKind.SourceChanged, "The file on the server has changed since the download started.", false);
        }

        if (response.Content.Headers.ContentEncoding.Any(e => !string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DownloadException(DownloadErrorKind.InvalidRangeResponse, "The server compressed a byte-range response.", false);
        }

        if (response.Content.Headers.ContentLength is { } length && length != last - from + 1)
        {
            throw new DownloadException(DownloadErrorKind.InvalidRangeResponse, "The byte-range response length does not match its Content-Range.", false);
        }

        return last;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RunState(int allowedWorkers)
    {
        private int _allowed = allowedWorkers;

        public HashSet<int> InProgress { get; } = [];
        public int ActiveWorkers { get; set; }
        public int AllowedWorkers => Volatile.Read(ref _allowed);

        /// <summary>Lowers the connection count by one (never below one). Returns true if it changed.</summary>
        public bool ReduceConcurrency()
        {
            while (true)
            {
                var current = Volatile.Read(ref _allowed);
                if (current <= 1)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _allowed, current - 1, current) == current)
                {
                    return true;
                }
            }
        }
    }
}

/// <summary>Validates finished segment files and joins them in byte order.</summary>
internal static class SegmentAssembler
{
    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>
    /// Appends segments 2..n onto the first segment file and returns its path. Segment files are
    /// left in place until the caller finalizes, so an interrupted assembly can be redone.
    /// </summary>
    public static async Task<string> AssembleAsync(DownloadItem item, string temporaryDirectory, long totalSize, CancellationToken cancellationToken)
    {
        List<DownloadSegment> segments;
        lock (item.SyncRoot)
        {
            segments = item.Segments.Select(s => s.Clone()).OrderBy(s => s.StartOffset).ToList();
        }

        if (!SegmentCalculator.IsValidLayout(segments, totalSize))
        {
            throw new DownloadException(DownloadErrorKind.VerificationFailed, "The downloaded parts do not cover the whole file.", false);
        }

        foreach (var segment in segments)
        {
            var path = Path.Combine(temporaryDirectory, segment.TempFileName);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new DownloadException(DownloadErrorKind.PartialDataMissing, "A downloaded part of the file is missing.", false);
            }

            if (segment.DownloadedBytes != segment.Length || info.Length < segment.Length)
            {
                throw new DownloadException(DownloadErrorKind.VerificationFailed, $"Part {segment.Index + 1} is incomplete ({info.Length:N0} of {segment.Length:N0} bytes).", false);
            }
        }

        var first = segments[0];
        var targetPath = Path.Combine(temporaryDirectory, first.TempFileName);
        try
        {
            await using var target = new FileStream(targetPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 0,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

            // Undo any partial append left by an earlier, interrupted assembly.
            target.SetLength(first.Length);
            target.Seek(0, SeekOrigin.End);

            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                foreach (var segment in segments.Skip(1))
                {
                    var path = Path.Combine(temporaryDirectory, segment.TempFileName);
                    await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var remaining = segment.Length;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), CancellationToken.None).ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new DownloadException(DownloadErrorKind.VerificationFailed, $"Part {segment.Index + 1} ended unexpectedly during assembly.", false);
                        }

                        await target.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
                        remaining -= read;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            target.Flush(flushToDisk: true);
            if (target.Length != totalSize)
            {
                throw new DownloadException(DownloadErrorKind.VerificationFailed, $"The assembled file has the wrong size ({target.Length:N0} instead of {totalSize:N0} bytes).", false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw DownloadErrorClassifier.ClassifyFileSystem(ex);
        }

        return targetPath;
    }
}
