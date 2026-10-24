using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Http;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.StateMachine;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.Engine;

/// <summary>Services shared by all jobs, supplied by the coordinator.</summary>
internal sealed class JobServices
{
    public required HttpDownloadEngine Http { get; init; }
    public required SegmentedDownloadEngine Segments { get; init; }
    public required IFileSystemService FileSystem { get; init; }
    public required IDownloadRepository Repository { get; init; }
    public required Func<AppSettings> Settings { get; init; }
    public required BandwidthLimiter GlobalLimiter { get; init; }
    public required EngineOptions Options { get; init; }
    public required TimeProvider Time { get; init; }
    public required ILogger Logger { get; init; }
    public IMediaTool? MediaTool { get; init; }

    /// <summary>Publishes the item's current state to listeners.</summary>
    public required Action<DownloadItem> Publish { get; init; }

    /// <summary>
    /// Atomically picks a free file name in a folder and assigns it to the item (see DownloadCoordinator.ClaimFileName).
    /// Must be called without holding any item lock.
    /// </summary>
    public required Func<DownloadItem, string, string, ConflictPolicy, string?> ClaimFileName { get; init; }
}

/// <summary>
/// Runs one download from connection to finalization, including automatic retries.
/// A job is single-use: once <see cref="Completion"/> finishes, the coordinator creates a new job
/// for any further attempt.
/// </summary>
internal sealed class DownloadJob
{
    private const string StreamFileName = "stream.part";

    private readonly DownloadItem _item;
    private readonly JobServices _s;
    private readonly RequestContext _context;
    private readonly BandwidthLimiter _downloadLimiter;
    private readonly CancellationTokenSource _cts = new();
    private readonly SpeedMonitor _speed = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TransferCounters _counters;
    private int _stopReason = -1;
    private string? _notice;

    public DownloadJob(DownloadItem item, JobServices services, RequestContext context, BandwidthLimiter downloadLimiter, bool startedByQueue)
    {
        _item = item;
        _s = services;
        _context = context;
        _downloadLimiter = downloadLimiter;
        StartedByQueue = startedByQueue;
        _counters = new TransferCounters(item.DownloadedBytes);
    }

    public Guid Id => _item.Id;
    public bool StartedByQueue { get; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public StopReason? RequestedStop => _stopReason < 0 ? null : (StopReason)_stopReason;

    public void Start() => Completion = Task.Run(RunAsync);

    /// <summary>Asks the job to stop. Cancel overrides an earlier pause request.</summary>
    public void RequestStop(StopReason reason)
    {
        if (reason == StopReason.Cancel)
        {
            Interlocked.Exchange(ref _stopReason, (int)reason);
        }
        else
        {
            Interlocked.CompareExchange(ref _stopReason, (int)reason, -1);
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private IReadOnlyList<BandwidthLimiter> Limiters => [_downloadLimiter, _s.GlobalLimiter];

    private async Task RunAsync()
    {
        using var progressCts = new CancellationTokenSource();
        var progressLoop = Task.Run(() => ProgressLoopAsync(progressCts.Token));
        var failures = 0;
        var sourceChangeRestarts = 0;
        var forceSingleConnection = false;
        var reResolved = false;
        try
        {
            while (true)
            {
                DownloadException error;
                try
                {
                    if (_item.Kind == DownloadKind.Media)
                    {
                        await ExecuteMediaAttemptAsync(_cts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await ExecuteAttemptAsync(forceSingleConnection, _cts.Token).ConfigureAwait(false);
                    }

                    return;
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    await HandleStopAsync().ConfigureAwait(false);
                    return;
                }
                catch (InvalidStateTransitionException ex)
                {
                    _s.Logger.LogError(ex, "Invalid state transition for download {Id}", _item.Id);
                    error = new DownloadException(DownloadErrorKind.Unknown, "Internal error: " + ex.Message, false, inner: ex);
                }
                catch (Exception ex)
                {
                    error = DownloadErrorClassifier.Classify(ex);
                }

                _s.Logger.LogWarning(error.InnerException, "Download {Id} attempt failed: {Kind} {Message}", _item.Id, error.Kind, error.Message);

                if (error.Kind == DownloadErrorKind.DestinationUnavailable)
                {
                    await EnterWaitingForDestinationAsync(error).ConfigureAwait(false);
                    return;
                }

                if (error.Kind is DownloadErrorKind.SourceChanged or DownloadErrorKind.PartialDataMissing && sourceChangeRestarts < 2)
                {
                    sourceChangeRestarts++;
                    _notice = error.Kind == DownloadErrorKind.SourceChanged
                        ? "The file on the server changed, so the download restarted from the beginning."
                        : "Partial data was missing, so the download restarted from the beginning.";
                    DiscardPartialData();
                    continue;
                }

                if (error.Kind == DownloadErrorKind.InvalidRangeResponse && !forceSingleConnection)
                {
                    forceSingleConnection = true;
                    _notice = "The server did not handle byte ranges reliably, so a single connection is used.";
                    DiscardPartialData();
                    continue;
                }

                // Redirect targets (e.g. signed CDN links) can expire; resolve the original URL once more.
                if (error.Kind is DownloadErrorKind.Forbidden or DownloadErrorKind.NotFound or DownloadErrorKind.Unauthorized &&
                    !reResolved && _item.FinalUrl is not null && _item.FinalUrl != _item.SourceUrl)
                {
                    reResolved = true;
                    continue;
                }

                failures++;
                var policy = new RetryPolicy(_s.Settings().MaxRetries, _s.Options.RetryBaseDelay);
                if (policy.CanRetry(failures, error))
                {
                    if (!await WaitBeforeRetryAsync(error, policy.GetDelay(failures, error.RetryAfter)).ConfigureAwait(false))
                    {
                        await HandleStopAsync().ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                await FailAsync(error).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            // Last-resort guard: never leave a download stuck in an active state.
            _s.Logger.LogError(ex, "Unhandled error in download {Id}", _item.Id);
            await FailAsync(new DownloadException(DownloadErrorKind.Unknown, "An unexpected error occurred: " + ex.Message, false, inner: ex)).ConfigureAwait(false);
        }
        finally
        {
            await progressCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await progressLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
            _s.Publish(_item);
        }
    }

    private async Task ExecuteAttemptAsync(bool forceSingleConnection, CancellationToken ct)
    {
        Transition(DownloadStatus.Connecting);
        var directory = _item.DestinationDirectory;
        EnsureDestinationUsable(directory, null);
        var tempDir = _s.FileSystem.EnsureTemporaryDirectory(directory, _item.Id);
        lock (_item.SyncRoot)
        {
            _item.TemporaryDirectory = tempDir;
        }

        using var probe = await _s.Http.ProbeAsync(_item.SourceUrl, _context, ct).ConfigureAwait(false);
        var result = probe.Result;
        bool resume;
        lock (_item.SyncRoot)
        {
            resume = !forceSingleConnection && _item.Segments.Count > 0 && _item.SupportsRangeRequests == true && _item.ExpectedSize is not null;
            if (resume && !IsSameRepresentation(result))
            {
                resume = false;
                _notice = "The file on the server changed, so the download restarted from the beginning.";
            }

            if (!resume)
            {
                _item.Segments.Clear();
                _item.DownloadedBytes = 0;
            }

            _item.FinalUrl = result.FinalUrl;
            _item.ETag = result.ETag;
            _item.LastModified = result.LastModified;
            _item.ExpectedSize = result.ContentLength;
            _item.SupportsRangeRequests = result.SupportsRanges && !forceSingleConnection;
            _item.HttpStatusCode = result.StatusCode;
        }

        // Outside the item lock: conflict resolution consults every other download's state.
        ApplyServerFileName(result);

        if (resume)
        {
            bool lost;
            lock (_item.SyncRoot)
            {
                lost = ResumeManager.VerifyPartialData(_item);
            }

            if (lost)
            {
                _notice = "Some partial data was missing; those parts were downloaded again.";
            }
        }
        else
        {
            ClearTemporaryFiles(tempDir);
        }

        var size = result.ContentLength;
        var segmented = _item.SupportsRangeRequests == true && size is > 0;
        if (segmented && !resume)
        {
            var connections = _item.AccelerationEnabled ? Math.Clamp(_item.MaxConnections, 1, SegmentCalculator.MaxConnections) : 1;
            lock (_item.SyncRoot)
            {
                _item.Segments = SegmentCalculator.CreateSegments(size!.Value, connections, _s.Options.MinimumSegmentSize);
            }
        }

        EnsureDestinationUsable(directory, RequiredSpace(size, segmented));

        Transition(DownloadStatus.Downloading);
        lock (_item.SyncRoot)
        {
            _item.StartedAt ??= _s.Time.GetUtcNow();
            _item.LastError = _notice;
            _item.LastErrorKind = null;
        }

        await SaveAsync().ConfigureAwait(false);
        await RecordEventAsync(resume ? "Resumed" : "Started", LogRedactor.Url(result.FinalUrl)).ConfigureAwait(false);

        string assembledPath;
        if (segmented)
        {
            _counters = new TransferCounters(_item.Segments.Sum(s => s.DownloadedBytes));
            var run = new SegmentRun
            {
                Item = _item,
                Url = result.FinalUrl,
                Context = _context,
                TemporaryDirectory = tempDir,
                TotalSize = size!.Value,
                MaxConnections = _item.AccelerationEnabled ? _item.MaxConnections : 1,
                Counters = _counters,
                Limiters = Limiters,
                StrongETag = probe.StrongETag,
                LastModified = probe.StrongETag is null ? result.LastModified : null,
            };
            await _s.Segments.RunAsync(run, ct).ConfigureAwait(false);
            Transition(DownloadStatus.Verifying);
            assembledPath = await SegmentAssembler.AssembleAsync(_item, tempDir, size.Value, ct).ConfigureAwait(false);
        }
        else if (size == 0)
        {
            assembledPath = Path.Combine(tempDir, StreamFileName);
            await File.WriteAllBytesAsync(assembledPath, [], ct).ConfigureAwait(false);
            Transition(DownloadStatus.Verifying);
        }
        else
        {
            var response = probe.FullResponse;
            probe.FullResponse = null;
            response ??= await _s.Http.GetFullAsync(_item.SourceUrl, _context, ct).ConfigureAwait(false);
            using (response)
            {
                lock (_item.SyncRoot)
                {
                    _item.SupportsRangeRequests = false;
                    _item.Segments.Clear();
                    _item.ExpectedSize = HttpDownloadEngine.GetIdentityContentLength(response);
                }

                assembledPath = Path.Combine(tempDir, StreamFileName);
                _counters = new TransferCounters(0);
                var written = await _s.Http.DownloadSingleAsync(response, assembledPath, _counters, Limiters, ct).ConfigureAwait(false);
                lock (_item.SyncRoot)
                {
                    _item.ExpectedSize ??= written;
                    _item.DownloadedBytes = written;
                }
            }

            Transition(DownloadStatus.Verifying);
        }

        await FinalizeAsync(assembledPath, tempDir, ct).ConfigureAwait(false);
    }

    private async Task ExecuteMediaAttemptAsync(CancellationToken ct)
    {
        var tool = _s.MediaTool ?? throw new DownloadException(DownloadErrorKind.MediaToolFailed, "The media tool (yt-dlp) is not configured.", false);
        Transition(DownloadStatus.Connecting);
        var directory = _item.DestinationDirectory;
        EnsureDestinationUsable(directory, null);
        var tempDir = _s.FileSystem.EnsureTemporaryDirectory(directory, _item.Id);
        lock (_item.SyncRoot)
        {
            _item.TemporaryDirectory = tempDir;
            _item.StartedAt ??= _s.Time.GetUtcNow();
            _item.LastError = null;
        }

        Transition(DownloadStatus.Downloading);
        await SaveAsync().ConfigureAwait(false);
        _counters = new TransferCounters(0);
        var progress = new Progress<MediaProgress>(p =>
        {
            _counters.Set(p.DownloadedBytes);
            lock (_item.SyncRoot)
            {
                if (p.TotalBytes is > 0)
                {
                    _item.ExpectedSize = p.TotalBytes;
                }
            }
        });

        // Media items are named after the title; the tool appends the real extension.
        var stem = _item.FileName;
        var limit = MinLimit(_downloadLimiter.Limit, _s.GlobalLimiter.Limit);
        string produced;
        try
        {
            produced = await tool.DownloadAsync(_item.SourceUrl, tempDir, stem, _item.MediaFormat ?? "best", limit, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DownloadException)
        {
            throw new DownloadException(DownloadErrorKind.MediaToolFailed, ex.Message, false, inner: ex);
        }

        var producedName = FileNameSanitizer.Sanitize(Path.GetFileName(produced));
        lock (_item.SyncRoot)
        {
            _item.FileName = producedName;
            var length = new FileInfo(produced).Length;
            _item.ExpectedSize = length;
            _item.DownloadedBytes = length;
        }

        Transition(DownloadStatus.Verifying);
        await FinalizeAsync(produced, tempDir, ct).ConfigureAwait(false);
    }

    /// <summary>Verifies size and checksum, moves the file into place and marks the download completed.</summary>
    private async Task FinalizeAsync(string assembledPath, string tempDir, CancellationToken ct)
    {
        var length = new FileInfo(assembledPath).Length;
        long? expected;
        string? expectedHash;
        lock (_item.SyncRoot)
        {
            expected = _item.ExpectedSize;
            expectedHash = _item.ExpectedSha256;
        }

        if (expected is { } total && length != total)
        {
            throw new DownloadException(DownloadErrorKind.VerificationFailed, $"The downloaded file has the wrong size ({length:N0} instead of {total:N0} bytes).", false);
        }

        string? hash = null;
        if (expectedHash is not null || _s.Settings().ComputeSha256)
        {
            hash = await ComputeSha256Async(assembledPath, ct).ConfigureAwait(false);
            if (expectedHash is not null && !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                lock (_item.SyncRoot)
                {
                    _item.Sha256 = hash;
                }

                throw new DownloadException(DownloadErrorKind.ChecksumMismatch, "The SHA-256 checksum does not match the expected value. The file was not saved.", false);
            }
        }

        // From here on the move must not be interrupted halfway.
        string directory;
        string fileName;
        ConflictPolicy policy;
        lock (_item.SyncRoot)
        {
            directory = _item.DestinationDirectory;
            fileName = _item.FileName;
            policy = _item.ConflictPolicy;
        }

        var replace = policy == ConflictPolicy.Replace;
        string finalName;
        string finalPath;
        for (var attempt = 1; ; attempt++)
        {
            finalName = replace ? fileName : _s.ClaimFileName(_item, directory, fileName, ConflictPolicy.Rename) ?? fileName;
            finalPath = Path.Combine(directory, finalName);
            try
            {
                _s.FileSystem.FinalizeFile(assembledPath, finalPath, replace);
                break;
            }
            catch (IOException) when (!replace && attempt < 10 && File.Exists(finalPath))
            {
                // Another program created the file between the check and the move; pick the next free name.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw DownloadErrorClassifier.ClassifyFileSystem(ex);
            }
        }

        _s.FileSystem.ApplyMarkOfTheWeb(finalPath, _item.FinalUrl ?? _item.SourceUrl, _item.PageUrl);
        _s.FileSystem.DeleteTemporaryData(tempDir);

        lock (_item.SyncRoot)
        {
            _item.FileName = finalName;
            _item.Sha256 = hash;
            _item.ExpectedSize = length;
            _item.DownloadedBytes = length;
            _item.CompletedAt = _s.Time.GetUtcNow();
            _item.TemporaryDirectory = null;
            _item.Segments.Clear();
            _item.LastError = finalName != fileName ? $"Saved as \"{finalName}\" because \"{fileName}\" already existed." : _notice;
            _item.LastErrorKind = null;
            _item.CurrentSpeed = 0;
            _item.ActiveConnectionCount = 0;
            _item.FileMissing = false;
        }

        Transition(DownloadStatus.Completed);
        await SaveAsync().ConfigureAwait(false);
        await RecordEventAsync("Completed", $"{length} bytes").ConfigureAwait(false);
    }

    private async Task HandleStopAsync()
    {
        var reason = RequestedStop ?? StopReason.Pause;
        string? tempToDelete = null;
        lock (_item.SyncRoot)
        {
            foreach (var segment in _item.Segments)
            {
                segment.State = segment.IsComplete ? SegmentState.Completed : SegmentState.Pending;
            }

            _item.CurrentSpeed = 0;
            _item.ActiveConnectionCount = 0;

            if (reason == StopReason.Cancel)
            {
                tempToDelete = _item.TemporaryDirectory;
                _item.TemporaryDirectory = null;
                _item.Segments.Clear();
                _item.DownloadedBytes = 0;
                SetStatus(DownloadStatus.Canceled);
            }
            else
            {
                if (_item.Kind == DownloadKind.Http && !_item.IsResumable)
                {
                    tempToDelete = _item.TemporaryDirectory;
                    _item.TemporaryDirectory = null;
                    _item.Segments.Clear();
                    if (_item.DownloadedBytes > 0)
                    {
                        _item.LastError = "This server does not support resuming, so the download will start over.";
                    }

                    _item.DownloadedBytes = 0;
                }
                else if (_item.Segments.Count > 0)
                {
                    _item.DownloadedBytes = _item.Segments.Sum(s => s.DownloadedBytes);
                }

                SetStatus(reason == StopReason.Shutdown ? DownloadStatus.Interrupted : DownloadStatus.Paused);
            }
        }

        _s.FileSystem.DeleteTemporaryData(tempToDelete);
        await SaveAsync().ConfigureAwait(false);
        await RecordEventAsync(reason switch
        {
            StopReason.Cancel => "Canceled",
            StopReason.Shutdown => "Interrupted",
            _ => "Paused",
        }, null).ConfigureAwait(false);
        _s.Publish(_item);
    }

    private async Task EnterWaitingForDestinationAsync(DownloadException error)
    {
        lock (_item.SyncRoot)
        {
            foreach (var segment in _item.Segments)
            {
                segment.DownloadedBytes = segment.DurableBytes;
                segment.State = SegmentState.Pending;
            }

            if (_item.Segments.Count > 0)
            {
                _item.DownloadedBytes = _item.Segments.Sum(s => s.DownloadedBytes);
            }

            _item.LastError = error.Message;
            _item.LastErrorKind = error.Kind;
            _item.CurrentSpeed = 0;
            _item.ActiveConnectionCount = 0;
            SetStatus(DownloadStatus.WaitingForDestination);
        }

        await SaveAsync().ConfigureAwait(false);
        await RecordEventAsync("WaitingForDestination", error.Message).ConfigureAwait(false);
        _s.Publish(_item);
    }

    private async Task FailAsync(DownloadException error)
    {
        string? tempToDelete = null;
        lock (_item.SyncRoot)
        {
            _item.LastError = error.Message;
            _item.LastErrorKind = error.Kind;
            _item.HttpStatusCode = error.HttpStatusCode ?? _item.HttpStatusCode;
            _item.CurrentSpeed = 0;
            _item.ActiveConnectionCount = 0;
            if (!_item.IsResumable || error.Kind is DownloadErrorKind.ChecksumMismatch or DownloadErrorKind.VerificationFailed)
            {
                tempToDelete = _item.TemporaryDirectory;
                _item.TemporaryDirectory = null;
                _item.Segments.Clear();
                _item.DownloadedBytes = 0;
            }

            SetStatus(DownloadStatus.Failed);
        }

        _s.FileSystem.DeleteTemporaryData(tempToDelete);
        await SaveAsync().ConfigureAwait(false);
        await RecordEventAsync("Failed", error.Message).ConfigureAwait(false);
        _s.Publish(_item);
    }

    /// <summary>Shows Retrying and waits. Returns false if a stop was requested while waiting.</summary>
    private async Task<bool> WaitBeforeRetryAsync(DownloadException error, TimeSpan delay)
    {
        lock (_item.SyncRoot)
        {
            _item.RetryCount++;
            _item.LastError = $"{error.Message} Retrying in {Math.Ceiling(delay.TotalSeconds)} s.";
            _item.LastErrorKind = error.Kind;
            _item.HttpStatusCode = error.HttpStatusCode ?? _item.HttpStatusCode;
            _item.CurrentSpeed = 0;
            foreach (var segment in _item.Segments)
            {
                segment.State = segment.IsComplete ? SegmentState.Completed : SegmentState.Pending;
            }

            SetStatus(DownloadStatus.Retrying);
        }

        await SaveAsync().ConfigureAwait(false);
        _s.Publish(_item);
        try
        {
            await Task.Delay(delay, _s.Time, _cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ProgressLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_s.Options.ProgressInterval, _s.Time);
        var sinceCheckpoint = Stopwatch.StartNew();
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            bool downloading;
            lock (_item.SyncRoot)
            {
                downloading = _item.Status == DownloadStatus.Downloading;
                if (downloading)
                {
                    var bytes = _counters.Bytes;
                    _speed.AddSample(_clock.Elapsed, bytes);
                    _item.DownloadedBytes = bytes;
                    _item.CurrentSpeed = _speed.CurrentSpeed;
                    _item.AverageSpeed = _speed.AverageSpeed;
                    _item.ActiveConnectionCount = _counters.Connections;
                }
            }

            if (!downloading)
            {
                continue;
            }

            _s.Publish(_item);
            if (sinceCheckpoint.Elapsed >= _s.Options.CheckpointInterval)
            {
                sinceCheckpoint.Restart();
                await SaveProgressAsync().ConfigureAwait(false);
            }
        }
    }

    private bool IsSameRepresentation(ProbeResult result)
    {
        if (!result.SupportsRanges || result.ContentLength != _item.ExpectedSize)
        {
            return false;
        }

        if (_item.ETag is not null && result.ETag is not null && _item.ETag != result.ETag)
        {
            return false;
        }

        return _item.LastModified is null || result.LastModified is null || _item.LastModified == result.LastModified;
    }

    /// <summary>
    /// Uses the server's Content-Disposition name when the user did not choose one. Must not be called while
    /// holding the item lock: <see cref="JobServices.ClaimFileName"/> briefly locks every other download.
    /// </summary>
    private void ApplyServerFileName(ProbeResult result)
    {
        string directory;
        string current;
        lock (_item.SyncRoot)
        {
            if (!_item.AutoFileName || string.IsNullOrEmpty(result.SuggestedFileName))
            {
                return;
            }

            _item.AutoFileName = false;
            directory = _item.DestinationDirectory;
            current = _item.FileName;
        }

        if (string.Equals(current, result.SuggestedFileName, StringComparison.Ordinal))
        {
            return;
        }

        _s.ClaimFileName(_item, directory, result.SuggestedFileName, ConflictPolicy.Rename);
    }

    private long? RequiredSpace(long? size, bool segmented)
    {
        if (size is not { } total)
        {
            return null;
        }

        lock (_item.SyncRoot)
        {
            var remaining = Math.Max(0, total - _item.Segments.Sum(s => s.DownloadedBytes));
            // Assembly appends every later segment onto the first one before the parts are removed.
            var assembly = segmented && _item.Segments.Count > 1 ? total - _item.Segments.OrderBy(s => s.StartOffset).First().Length : 0;
            return remaining + assembly;
        }
    }

    private void EnsureDestinationUsable(string directory, long? requiredBytes)
    {
        var check = _s.FileSystem.CheckDestination(directory, requiredBytes);
        if (!check.IsUsable)
        {
            var kind = check.ErrorKind ?? DownloadErrorKind.AccessDenied;
            throw new DownloadException(kind, check.Message ?? "The destination folder cannot be used.", false);
        }
    }

    private void DiscardPartialData()
    {
        string? temp;
        lock (_item.SyncRoot)
        {
            temp = _item.TemporaryDirectory;
            _item.Segments.Clear();
            _item.DownloadedBytes = 0;
        }

        if (temp is not null)
        {
            ClearTemporaryFiles(temp);
        }
    }

    private void ClearTemporaryFiles(string tempDir)
    {
        try
        {
            if (!Directory.Exists(tempDir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(tempDir, "*.part"))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw DownloadErrorClassifier.ClassifyFileSystem(ex);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static long? MinLimit(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private void Transition(DownloadStatus to)
    {
        lock (_item.SyncRoot)
        {
            if (_item.Status == to)
            {
                return;
            }

            DownloadStateMachine.EnsureCanTransition(_item.Status, to);
            _item.Status = to;
            if (to != DownloadStatus.Downloading)
            {
                _item.CurrentSpeed = 0;
            }
        }

        _s.Publish(_item);
    }

    /// <summary>Stop/fail paths: apply the transition, logging rather than throwing if it is unexpected.</summary>
    private void SetStatus(DownloadStatus to)
    {
        if (_item.Status != to && !DownloadStateMachine.CanTransition(_item.Status, to))
        {
            _s.Logger.LogWarning("Unexpected transition {From} -> {To} for {Id}", _item.Status, to, _item.Id);
        }

        _item.Status = to;
    }

    private async Task SaveAsync()
    {
        try
        {
            await _s.Repository.SaveAsync(_item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _s.Logger.LogError(ex, "Could not save download {Id}", _item.Id);
        }
    }

    private async Task SaveProgressAsync()
    {
        try
        {
            // Segment layout can change through dynamic splitting, so persist the full record.
            await _s.Repository.SaveAsync(_item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _s.Logger.LogWarning(ex, "Could not checkpoint download {Id}", _item.Id);
        }
    }

    private async Task RecordEventAsync(string type, string? detail)
    {
        try
        {
            await _s.Repository.AddEventAsync(new DownloadEvent(_item.Id, _s.Time.GetUtcNow(), type, detail)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _s.Logger.LogWarning(ex, "Could not record event for {Id}", _item.Id);
        }
    }
}
