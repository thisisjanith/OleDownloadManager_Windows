using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.StateMachine;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.Engine;

/// <summary>
/// Owns all downloads, the persistent queue and the active transfer jobs. Scheduling decisions are
/// serialized through a single async gate, so the global active-download limit cannot be exceeded
/// by concurrent requests.
/// </summary>
public sealed class DownloadCoordinator : IDownloadCoordinator, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, DownloadItem> _items = new();
    private readonly ConcurrentDictionary<Guid, BandwidthLimiter> _limiters = new();
    private readonly ConcurrentDictionary<Guid, SessionSecrets> _secrets = new();

    // Guarded by _gate.
    private readonly Dictionary<Guid, DownloadJob> _jobs = [];
    private readonly HashSet<Guid> _manualStarts = [];
    private readonly HashSet<Guid> _pausedByQueue = [];

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Serializes file-name claims. Lock order: _nameLock, then item locks.</summary>
    private readonly object _nameLock = new();
    private readonly IDownloadRepository _repository;
    private readonly ISettingsStore _settings;
    private readonly IFileSystemService _fileSystem;
    private readonly ICredentialStore _credentials;
    private readonly INotificationService _notifications;
    private readonly EngineOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly BandwidthLimiter _globalLimiter = new();
    private readonly JobServices _services;
    private readonly HttpDownloadEngine _http;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _maintenance = Task.CompletedTask;
    private DateTimeOffset _lastMaintenance;
    private volatile bool _queueRunning;
    private volatile bool _shuttingDown;
    private bool _initialized;
    private int _activeCount;

    public DownloadCoordinator(HttpClient httpClient, IDownloadRepository repository, ISettingsStore settings,
        IFileSystemService fileSystem, ICredentialStore credentials, INotificationService notifications,
        ILoggerFactory loggerFactory, EngineOptions? options = null, TimeProvider? timeProvider = null, IMediaTool? mediaTool = null)
    {
        _repository = repository;
        _settings = settings;
        _fileSystem = fileSystem;
        _credentials = credentials;
        _notifications = notifications;
        _options = options ?? new EngineOptions();
        _time = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<DownloadCoordinator>();

        var engineLogger = loggerFactory.CreateLogger("OleDownloadManager.Engine");
        var sender = new HttpRequestSender(httpClient, _options, engineLogger);
        _http = new HttpDownloadEngine(sender, _options, engineLogger);
        _services = new JobServices
        {
            Http = _http,
            Segments = new SegmentedDownloadEngine(sender, _options, engineLogger),
            FileSystem = fileSystem,
            Repository = repository,
            Settings = () => _settings.Current,
            GlobalLimiter = _globalLimiter,
            Options = _options,
            Time = _time,
            Logger = engineLogger,
            MediaTool = mediaTool,
            Publish = Publish,
            ClaimFileName = ClaimFileName,
        };

        _settings.SettingsChanged += OnSettingsChanged;
    }

    public event EventHandler<DownloadSnapshot>? DownloadUpdated;

    public event EventHandler<Guid>? DownloadRemoved;

    public event EventHandler? QueueStateChanged;

    public bool IsQueueRunning => _queueRunning;

    public int ActiveCount => Volatile.Read(ref _activeCount);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var loaded = await _repository.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var settings = _settings.Current;
        var now = _time.GetUtcNow();
        _queueRunning = settings.QueueRunning;
        _globalLimiter.SetLimit(settings.GlobalSpeedLimit);

        var missed = 0;
        var resumed = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var item in loaded)
            {
                ResumeManager.ReconcileOnStartup(item);
                lock (item.SyncRoot)
                {
                    if (item.Status == DownloadStatus.Interrupted && item.DeletedAt is null && settings.AutoResumeOnStartup)
                    {
                        item.Status = DownloadStatus.Queued;
                        _manualStarts.Add(item.Id);
                        resumed++;
                    }

                    if (item.Status == DownloadStatus.Queued && item.DeletedAt is null && item.ScheduledAt is { } scheduled && scheduled <= now)
                    {
                        missed++;
                        if (!settings.RunMissedSchedules)
                        {
                            item.ScheduledAt = null;
                            item.Status = DownloadStatus.Paused;
                            item.LastError = "Missed its scheduled start time while OleDownloadManager was closed.";
                        }
                    }
                }

                _items[item.Id] = item;
                _limiters[item.Id] = new BandwidthLimiter(item.SpeedLimit, _time);
                await _repository.SaveAsync(item, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Loaded {Count} downloads ({Resumed} resuming, {Missed} missed schedules)", loaded.Count, resumed, missed);
        if (missed > 0)
        {
            var message = settings.RunMissedSchedules
                ? $"{missed} scheduled download(s) missed their start time while the app was closed and are starting now."
                : $"{missed} scheduled download(s) missed their start time while the app was closed and were paused.";
            _notifications.Notify("Missed schedule", message, NotificationKind.Info);
        }

        _lastMaintenance = _time.GetLocalNow();
        _maintenance = Task.Run(() => MaintenanceLoopAsync(_lifetime.Token), CancellationToken.None);
        await PumpAsync().ConfigureAwait(false);
    }

    public IReadOnlyList<DownloadSnapshot> GetAll() => _items.Values.Select(i => i.ToSnapshot()).ToList();

    public DownloadSnapshot? Get(Guid id) => _items.TryGetValue(id, out var item) ? item.ToSnapshot() : null;

    public DownloadSnapshot? FindActiveDuplicate(Uri url) =>
        _items.Values
            .Where(i => i.DeletedAt is null && i.Status is not (DownloadStatus.Completed or DownloadStatus.Canceled) && i.SourceUrl == url)
            .Select(i => i.ToSnapshot())
            .FirstOrDefault();

    public async Task<ProbeResult> ProbeAsync(Uri url, IReadOnlyDictionary<string, string>? headers, DownloadCredentials? credentials, CancellationToken cancellationToken)
    {
        using var outcome = await _http.ProbeAsync(url, new RequestContext(url, headers, credentials, null), cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    public async Task<AddDownloadResult> AddAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        if (_shuttingDown)
        {
            return AddDownloadResult.Rejected("OleDownloadManager is shutting down.");
        }

        if (!UrlValidator.IsHttpScheme(request.Url) || !string.IsNullOrEmpty(request.Url.UserInfo))
        {
            return AddDownloadResult.Rejected("Only HTTP and HTTPS URLs without embedded credentials are supported.");
        }

        if (request.ExpectedSha256 is { Length: > 0 } sha && (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit)))
        {
            return AddDownloadResult.Rejected("The expected SHA-256 checksum must be 64 hexadecimal characters.");
        }

        var settings = _settings.Current;
        var classifier = new CategoryClassifier(settings.CategoryExtensions);
        var autoName = string.IsNullOrWhiteSpace(request.FileName);
        var fileName = autoName ? FileNameSanitizer.FromUrl(request.Url) : FileNameSanitizer.Sanitize(request.FileName);
        var category = request.Category ?? classifier.Classify(fileName);

        string directory;
        try
        {
            directory = Path.GetFullPath(request.DestinationDirectory ?? DefaultDirectoryFor(category, settings));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return AddDownloadResult.Rejected("The destination folder path is not valid.");
        }

        var policy = request.ConflictPolicy ?? settings.DefaultConflictPolicy;
        var resolved = _fileSystem.ResolveConflict(directory, fileName, policy, ReservedPaths(Guid.Empty));
        if (resolved is null)
        {
            return AddDownloadResult.Rejected($"\"{fileName}\" already exists in the destination folder, so it was skipped.");
        }

        var id = Guid.NewGuid();
        var plainHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secretHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in request.Headers)
        {
            if (HeaderValidator.IsForbidden(name))
            {
                continue;
            }

            (HeaderValidator.IsSensitive(name) ? secretHeaders : plainHeaders)[name] = value;
        }

        var item = new DownloadItem
        {
            Id = id,
            Kind = request.Kind,
            SourceUrl = request.Url,
            PageUrl = request.PageUrl,
            FileName = resolved,
            AutoFileName = autoName && request.Kind == DownloadKind.Http,
            DestinationDirectory = directory,
            Category = category,
            Priority = request.Priority,
            Source = request.Source,
            ConflictPolicy = policy,
            CreatedAt = _time.GetUtcNow(),
            ScheduledAt = request.ScheduledAt,
            AccelerationEnabled = request.AccelerationEnabled,
            MaxConnections = Math.Clamp(request.MaxConnections ?? settings.DefaultConnections, 1, 8),
            SpeedLimit = request.SpeedLimit ?? settings.DefaultPerDownloadSpeedLimit,
            ExpectedSha256 = string.IsNullOrWhiteSpace(request.ExpectedSha256) ? null : request.ExpectedSha256.ToLowerInvariant(),
            ExpectedSize = request.KnownSize,
            MediaFormat = request.MediaFormat,
            Headers = plainHeaders,
            QueuePosition = _items.IsEmpty ? 1 : _items.Values.Max(i => i.QueuePosition) + 1,
        };

        if (secretHeaders.Count > 0 || request.Credentials is not null)
        {
            var secrets = new SessionSecrets(secretHeaders, request.Credentials?.UserName, request.Credentials?.Password);
            _secrets[id] = secrets;
            if (request.RememberSecrets)
            {
                var key = $"OleDownloadManager:download:{id:N}";
                try
                {
                    _credentials.Save(key, JsonSerializer.Serialize(secrets));
                    item.CredentialKey = key;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not store credentials for {Id}", id);
                }
            }
        }

        // Claim the final name and publish the item atomically with respect to other claims.
        lock (_nameLock)
        {
            item.FileName = _fileSystem.ResolveConflict(directory, resolved, policy == ConflictPolicy.Skip ? ConflictPolicy.Rename : policy, ReservedPaths(Guid.Empty)) ?? resolved;
            _items[id] = item;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _limiters[id] = new BandwidthLimiter(item.SpeedLimit, _time);
            if (request.StartNow)
            {
                _manualStarts.Add(id);
            }
        }
        finally
        {
            _gate.Release();
        }

        await _repository.SaveAsync(item, CancellationToken.None).ConfigureAwait(false);
        await RecordEventAsync(id, "Added", $"{request.Source}: {LogRedactor.Url(request.Url)}").ConfigureAwait(false);
        _logger.LogInformation("Added download {Id} for {Url}", id, LogRedactor.Url(request.Url));
        Publish(item);
        await PumpAsync().ConfigureAwait(false);
        return AddDownloadResult.Success(id);
    }

    public async Task StartAsync(IEnumerable<Guid> ids)
    {
        await WithGateAsync(() =>
        {
            foreach (var item in Resolve(ids))
            {
                if (_jobs.ContainsKey(item.Id))
                {
                    continue;
                }

                lock (item.SyncRoot)
                {
                    if (item.DeletedAt is not null || !(item.Status == DownloadStatus.Failed || DownloadStateMachine.CanStart(item.Status)))
                    {
                        continue;
                    }

                    if (item.Status == DownloadStatus.Failed)
                    {
                        item.RetryCount = 0;
                    }

                    item.Status = DownloadStatus.Queued;
                    item.ScheduledAt = null;
                    _manualStarts.Add(item.Id);
                    _pausedByQueue.Remove(item.Id);
                }

                SaveInBackground(item);
                Publish(item);
            }

            PumpLocked();
        }).ConfigureAwait(false);
    }

    public Task PauseAsync(IEnumerable<Guid> ids) => StopAsync(ids, StopReason.Pause);

    public Task CancelAsync(IEnumerable<Guid> ids) => StopAsync(ids, StopReason.Cancel);

    public async Task RetryAsync(IEnumerable<Guid> ids)
    {
        await WithGateAsync(() =>
        {
            foreach (var item in Resolve(ids))
            {
                lock (item.SyncRoot)
                {
                    if (!DownloadStateMachine.CanRetry(item.Status))
                    {
                        continue;
                    }

                    item.Status = DownloadStatus.Queued;
                    item.RetryCount = 0;
                    item.LastError = null;
                    item.LastErrorKind = null;
                    _manualStarts.Add(item.Id);
                }

                SaveInBackground(item);
                Publish(item);
            }

            PumpLocked();
        }).ConfigureAwait(false);
    }

    public async Task RedownloadAsync(IEnumerable<Guid> ids)
    {
        await WithGateAsync(() =>
        {
            foreach (var item in Resolve(ids))
            {
                lock (item.SyncRoot)
                {
                    if (!DownloadStateMachine.CanTransition(item.Status, DownloadStatus.Queued, explicitUserAction: true))
                    {
                        continue;
                    }

                    item.Status = DownloadStatus.Queued;
                    item.DownloadedBytes = 0;
                    item.Segments.Clear();
                    item.CompletedAt = null;
                    item.StartedAt = null;
                    item.Sha256 = null;
                    item.RetryCount = 0;
                    item.LastError = null;
                    item.LastErrorKind = null;
                    item.FileMissing = false;
                    item.DeletedAt = null;
                    _manualStarts.Add(item.Id);
                }

                SaveInBackground(item);
                Publish(item);
            }

            PumpLocked();
        }).ConfigureAwait(false);
    }

    public async Task MoveToTrashAsync(IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        await StopAsync(list, StopReason.Pause).ConfigureAwait(false);
        await WithGateAsync(() =>
        {
            foreach (var item in Resolve(list))
            {
                lock (item.SyncRoot)
                {
                    item.DeletedAt = _time.GetUtcNow();
                    _manualStarts.Remove(item.Id);
                    _pausedByQueue.Remove(item.Id);
                }

                SaveInBackground(item);
                Publish(item);
            }
        }).ConfigureAwait(false);
    }

    public async Task RestoreFromTrashAsync(IEnumerable<Guid> ids)
    {
        await WithGateAsync(() =>
        {
            foreach (var item in Resolve(ids))
            {
                lock (item.SyncRoot)
                {
                    item.DeletedAt = null;
                    if (item.Status == DownloadStatus.Queued)
                    {
                        // Do not let a restored item start unexpectedly.
                        item.Status = DownloadStatus.Paused;
                    }
                }

                SaveInBackground(item);
                Publish(item);
            }
        }).ConfigureAwait(false);
    }

    public async Task DeletePermanentlyAsync(IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        await StopAsync(list, StopReason.Cancel).ConfigureAwait(false);
        foreach (var item in Resolve(list))
        {
            string? temp;
            string? credentialKey;
            lock (item.SyncRoot)
            {
                temp = item.TemporaryDirectory;
                credentialKey = item.CredentialKey;
            }

            _fileSystem.DeleteTemporaryData(temp);
            DeleteSecrets(item.Id, credentialKey);
            await _repository.DeleteAsync(item.Id).ConfigureAwait(false);
            _items.TryRemove(item.Id, out _);
            _limiters.TryRemove(item.Id, out _);
            DownloadRemoved?.Invoke(this, item.Id);
        }
    }

    public async Task DeleteIncompleteDataAsync(IEnumerable<Guid> ids)
    {
        await WithGateAsync(() =>
        {
            foreach (var item in Resolve(ids))
            {
                if (_jobs.ContainsKey(item.Id))
                {
                    continue;
                }

                string? temp;
                lock (item.SyncRoot)
                {
                    if (item.Status is DownloadStatus.Completed)
                    {
                        continue;
                    }

                    temp = item.TemporaryDirectory;
                    item.TemporaryDirectory = null;
                    item.Segments.Clear();
                    item.DownloadedBytes = 0;
                }

                _fileSystem.DeleteTemporaryData(temp);
                SaveInBackground(item);
                Publish(item);
            }
        }).ConfigureAwait(false);
    }

    public async Task SetPriorityAsync(IEnumerable<Guid> ids, DownloadPriority priority)
    {
        await WithGateAsync(() =>
        {
            foreach (var item in Resolve(ids))
            {
                lock (item.SyncRoot)
                {
                    item.Priority = priority;
                }

                SaveInBackground(item);
                Publish(item);
            }
        }).ConfigureAwait(false);
    }

    public async Task MoveInQueueAsync(Guid id, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        await WithGateAsync(() =>
        {
            if (!_items.TryGetValue(id, out var item))
            {
                return;
            }

            var ordered = _items.Values
                .Where(i => i.DeletedAt is null && i.Status is not (DownloadStatus.Completed or DownloadStatus.Canceled))
                .OrderBy(i => i.QueuePosition).ThenBy(i => i.CreatedAt).ToList();
            var index = ordered.IndexOf(item);
            var target = Math.Clamp(index + delta, 0, ordered.Count - 1);
            if (index < 0 || target == index)
            {
                return;
            }

            ordered.RemoveAt(index);
            ordered.Insert(target, item);
            var positions = new List<(Guid, long)>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                lock (ordered[i].SyncRoot)
                {
                    ordered[i].QueuePosition = i + 1;
                }

                positions.Add((ordered[i].Id, i + 1));
            }

            _ = PersistPositionsAsync(positions);
            foreach (var changed in ordered)
            {
                Publish(changed);
            }
        }).ConfigureAwait(false);
    }

    public async Task SetScheduleAsync(IEnumerable<Guid> ids, DateTimeOffset? scheduledAt)
    {
        await WithGateAsync(() =>
        {
            foreach (var item in Resolve(ids))
            {
                if (_jobs.ContainsKey(item.Id))
                {
                    continue;
                }

                lock (item.SyncRoot)
                {
                    if (item.Status is DownloadStatus.Paused or DownloadStatus.Interrupted or DownloadStatus.Queued or DownloadStatus.Failed)
                    {
                        item.Status = DownloadStatus.Queued;
                        item.ScheduledAt = scheduledAt;
                        _manualStarts.Remove(item.Id);
                    }
                }

                SaveInBackground(item);
                Publish(item);
            }

            PumpLocked();
        }).ConfigureAwait(false);
    }

    public async Task SetSpeedLimitAsync(IEnumerable<Guid> ids, long? bytesPerSecond)
    {
        var limit = bytesPerSecond is > 0 ? bytesPerSecond : null;
        await WithGateAsync(() =>
        {
            foreach (var item in Resolve(ids))
            {
                lock (item.SyncRoot)
                {
                    item.SpeedLimit = limit;
                }

                _limiters.GetOrAdd(item.Id, _ => new BandwidthLimiter(null, _time)).SetLimit(limit);
                SaveInBackground(item);
                Publish(item);
            }
        }).ConfigureAwait(false);
    }

    public async Task StartQueueAsync()
    {
        await WithGateAsync(() =>
        {
            _queueRunning = true;
            foreach (var item in Resolve(_pausedByQueue.ToList()))
            {
                lock (item.SyncRoot)
                {
                    if (item.Status == DownloadStatus.Paused)
                    {
                        item.Status = DownloadStatus.Queued;
                    }
                }

                SaveInBackground(item);
                Publish(item);
            }

            _pausedByQueue.Clear();
            PumpLocked();
        }).ConfigureAwait(false);

        await _settings.UpdateAsync(s => s.QueueRunning = true).ConfigureAwait(false);
        QueueStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopQueueAsync()
    {
        var waits = new List<Task>();
        await WithGateAsync(() =>
        {
            _queueRunning = false;
            foreach (var job in _jobs.Values.Where(j => j.StartedByQueue))
            {
                job.RequestStop(StopReason.Pause);
                _pausedByQueue.Add(job.Id);
                waits.Add(job.Completion);
            }
        }).ConfigureAwait(false);

        await Task.WhenAll(waits).ConfigureAwait(false);
        await _settings.UpdateAsync(s => s.QueueRunning = false).ConfigureAwait(false);
        QueueStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task PauseAllAsync()
    {
        var waits = new List<Task>();
        await WithGateAsync(() =>
        {
            _queueRunning = false;
            _manualStarts.Clear();
            foreach (var job in _jobs.Values)
            {
                job.RequestStop(StopReason.Pause);
                waits.Add(job.Completion);
            }
        }).ConfigureAwait(false);

        await Task.WhenAll(waits).ConfigureAwait(false);
        await _settings.UpdateAsync(s => s.QueueRunning = false).ConfigureAwait(false);
        QueueStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResumeAllAsync()
    {
        var ids = _items.Values
            .Where(i => i.DeletedAt is null && i.Status is DownloadStatus.Paused or DownloadStatus.Interrupted)
            .Select(i => i.Id)
            .ToList();
        await StartAsync(ids).ConfigureAwait(false);
        await StartQueueAsync().ConfigureAwait(false);
    }

    public async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        var waits = new List<Task>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var job in _jobs.Values)
            {
                job.RequestStop(StopReason.Shutdown);
                waits.Add(job.Completion);
            }
        }
        finally
        {
            _gate.Release();
        }

        var all = Task.WhenAll(waits);
        if (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false) != all)
        {
            _logger.LogWarning("Some downloads did not stop within the shutdown timeout");
        }

        try
        {
            await _maintenance.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _settings.SettingsChanged -= OnSettingsChanged;
        _lifetime.Dispose();
    }

    // ---------------------------------------------------------------- internals

    private async Task StopAsync(IEnumerable<Guid> ids, StopReason reason)
    {
        var waits = new List<Task>();
        await WithGateAsync(() =>
        {
            foreach (var item in Resolve(ids))
            {
                _manualStarts.Remove(item.Id);
                _pausedByQueue.Remove(item.Id);
                if (_jobs.TryGetValue(item.Id, out var job))
                {
                    job.RequestStop(reason);
                    waits.Add(job.Completion);
                    continue;
                }

                string? temp = null;
                string? credentialKey = null;
                var changed = false;
                lock (item.SyncRoot)
                {
                    if (reason == StopReason.Cancel && DownloadStateMachine.CanCancel(item.Status))
                    {
                        temp = item.TemporaryDirectory;
                        credentialKey = item.CredentialKey;
                        item.TemporaryDirectory = null;
                        item.Segments.Clear();
                        item.DownloadedBytes = 0;
                        item.Status = DownloadStatus.Canceled;
                        item.ScheduledAt = null;
                        changed = true;
                    }
                    else if (reason == StopReason.Pause && item.Status is DownloadStatus.Queued or DownloadStatus.Interrupted or DownloadStatus.WaitingForDestination)
                    {
                        item.Status = DownloadStatus.Paused;
                        item.ScheduledAt = null;
                        changed = true;
                    }
                }

                if (changed)
                {
                    _fileSystem.DeleteTemporaryData(temp);
                    if (reason == StopReason.Cancel)
                    {
                        DeleteSecrets(item.Id, credentialKey);
                    }

                    SaveInBackground(item);
                    Publish(item);
                }
            }
        }).ConfigureAwait(false);

        await Task.WhenAll(waits).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        await WithGateAsync(PumpLocked).ConfigureAwait(false);
    }

    /// <summary>Starts eligible downloads into free slots. Caller holds the gate.</summary>
    private void PumpLocked()
    {
        if (_shuttingDown || !_initialized)
        {
            return;
        }

        var settings = _settings.Current;
        var candidates = _items.Values.Where(i => !_jobs.ContainsKey(i.Id));
        var toStart = QueueScheduler.SelectToStart(candidates, _jobs.Count, _manualStarts, _queueRunning, settings.MaxActiveDownloads, _time.GetUtcNow());
        foreach (var item in toStart)
        {
            var manual = _manualStarts.Remove(item.Id);
            bool wasScheduled;
            lock (item.SyncRoot)
            {
                wasScheduled = item.ScheduledAt is not null;
                item.ScheduledAt = null;
            }

            if (wasScheduled && settings.NotificationsEnabled && settings.NotifyOnQueueStart)
            {
                _notifications.Notify("Scheduled download started", item.FileName, NotificationKind.Info);
            }

            var limiter = _limiters.GetOrAdd(item.Id, _ => new BandwidthLimiter(item.SpeedLimit, _time));
            var job = new DownloadJob(item, _services, BuildContext(item), limiter, startedByQueue: !manual && !wasScheduled);
            _jobs[item.Id] = job;
            Volatile.Write(ref _activeCount, _jobs.Count);
            job.Start();
            _ = job.Completion.ContinueWith(_ => OnJobCompletedAsync(job), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task OnJobCompletedAsync(DownloadJob job)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_jobs.TryGetValue(job.Id, out var current) && ReferenceEquals(current, job))
            {
                _jobs.Remove(job.Id);
                Volatile.Write(ref _activeCount, _jobs.Count);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (_items.TryGetValue(job.Id, out var item))
        {
            NotifyOutcome(item);
        }

        await PumpAsync().ConfigureAwait(false);
    }

    private void NotifyOutcome(DownloadItem item)
    {
        var settings = _settings.Current;
        if (!settings.NotificationsEnabled || _shuttingDown)
        {
            return;
        }

        var snapshot = item.ToSnapshot();
        switch (snapshot.Status)
        {
            case DownloadStatus.Completed when settings.NotifyOnComplete:
                _notifications.Notify("Download complete", snapshot.FileName, NotificationKind.Success);
                break;
            case DownloadStatus.Failed when settings.NotifyOnFailure:
                _notifications.Notify("Download failed", $"{snapshot.FileName}: {snapshot.LastError}", NotificationKind.Error);
                break;
            case DownloadStatus.WaitingForDestination:
                _notifications.Notify("Destination unavailable", $"{snapshot.FileName} is waiting for {snapshot.DestinationDirectory}. Reconnect the drive to continue.", NotificationKind.Warning);
                break;
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.MaintenanceInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await RunMaintenanceAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Queue maintenance failed");
            }
        }
    }

    /// <summary>Applies daily queue times, starts due scheduled downloads and re-checks missing destinations.</summary>
    internal async Task RunMaintenanceAsync()
    {
        var settings = _settings.Current;
        var now = _time.GetLocalNow();
        var previous = _lastMaintenance;
        _lastMaintenance = now;

        if (settings.QueueDailyStart is { } start && !_queueRunning && QueueScheduler.CrossedDailyTime(start, previous, now))
        {
            await StartQueueAsync().ConfigureAwait(false);
            if (settings.NotificationsEnabled && settings.NotifyOnQueueStart)
            {
                _notifications.Notify("Queue started", "The scheduled download queue has started.", NotificationKind.Info);
            }
        }

        if (settings.QueueDailyStop is { } stop && _queueRunning && QueueScheduler.CrossedDailyTime(stop, previous, now))
        {
            await StopQueueAsync().ConfigureAwait(false);
        }

        await WithGateAsync(() =>
        {
            foreach (var item in _items.Values)
            {
                if (_jobs.ContainsKey(item.Id))
                {
                    continue;
                }

                lock (item.SyncRoot)
                {
                    if (item.Status != DownloadStatus.WaitingForDestination || item.DeletedAt is not null ||
                        !ResumeManager.IsVolumeAvailable(item.DestinationDirectory))
                    {
                        continue;
                    }

                    var lost = ResumeManager.VerifyPartialData(item);
                    item.Status = DownloadStatus.Queued;
                    item.LastError = lost ? "The destination is back, but some partial data was missing and will be downloaded again." : null;
                    _manualStarts.Add(item.Id);
                }

                _logger.LogInformation("Destination for {Id} is available again", item.Id);
                SaveInBackground(item);
                Publish(item);
            }

            PumpLocked();
        }).ConfigureAwait(false);
    }

    private RequestContext BuildContext(DownloadItem item)
    {
        Dictionary<string, string> headers;
        string? credentialKey;
        lock (item.SyncRoot)
        {
            headers = new Dictionary<string, string>(item.Headers, StringComparer.OrdinalIgnoreCase);
            credentialKey = item.CredentialKey;
        }

        var secrets = _secrets.GetValueOrDefault(item.Id);
        if (secrets is null && credentialKey is not null)
        {
            try
            {
                var json = _credentials.Read(credentialKey);
                secrets = json is null ? null : JsonSerializer.Deserialize<SessionSecrets>(json);
                if (secrets is not null)
                {
                    _secrets[item.Id] = secrets;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read stored credentials for {Id}", item.Id);
            }
        }

        DownloadCredentials? credentials = null;
        if (secrets is not null)
        {
            foreach (var (name, value) in secrets.Headers ?? [])
            {
                headers[name] = value;
            }

            if (!string.IsNullOrEmpty(secrets.UserName))
            {
                credentials = new DownloadCredentials(secrets.UserName, secrets.Password ?? "");
            }
        }

        return new RequestContext(item.SourceUrl, headers, credentials, item.PageUrl);
    }

    private void DeleteSecrets(Guid id, string? credentialKey)
    {
        _secrets.TryRemove(id, out _);
        if (credentialKey is null)
        {
            return;
        }

        try
        {
            _credentials.Delete(credentialKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete stored credentials for {Id}", id);
        }
    }

    private string DefaultDirectoryFor(DownloadCategory category, AppSettings settings)
    {
        var root = string.IsNullOrWhiteSpace(settings.DefaultDownloadDirectory)
            ? _fileSystem.GetDefaultDownloadDirectory()
            : settings.DefaultDownloadDirectory;
        if (!settings.AutoOrganize)
        {
            return root;
        }

        if (settings.CategoryFolders.TryGetValue(category, out var folder) && !string.IsNullOrWhiteSpace(folder))
        {
            return folder;
        }

        return category == DownloadCategory.Other ? root : Path.Combine(root, category.ToString());
    }

    /// <summary>
    /// Picks a free name for <paramref name="item"/> in <paramref name="directory"/> and records it on the item,
    /// atomically with respect to other downloads claiming names. Returns null only for Skip when the file exists.
    /// Callers must not hold any item lock.
    /// </summary>
    private string? ClaimFileName(DownloadItem item, string directory, string desiredName, ConflictPolicy policy)
    {
        lock (_nameLock)
        {
            var resolved = _fileSystem.ResolveConflict(directory, desiredName, policy, ReservedPaths(item.Id));
            if (resolved is not null)
            {
                lock (item.SyncRoot)
                {
                    item.FileName = resolved;
                }
            }

            return resolved;
        }
    }

    /// <summary>
    /// Destination paths claimed by other unfinished downloads. Locks each item briefly in turn, so callers
    /// must not hold any item lock (that would allow lock-ordering deadlocks between jobs).
    /// </summary>
    private ISet<string> ReservedPaths(Guid exceptId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items.Values)
        {
            if (item.Id == exceptId)
            {
                continue;
            }

            lock (item.SyncRoot)
            {
                if (item.Status is not (DownloadStatus.Completed or DownloadStatus.Canceled) && item.DeletedAt is null)
                {
                    set.Add(item.DestinationPath);
                }
            }
        }

        return set;
    }

    private IEnumerable<DownloadItem> Resolve(IEnumerable<Guid> ids)
    {
        foreach (var id in ids.Distinct())
        {
            if (_items.TryGetValue(id, out var item))
            {
                yield return item;
            }
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        _globalLimiter.SetLimit(settings.GlobalSpeedLimit);
        _queueRunning = settings.QueueRunning;
        _ = PumpAsync();
    }

    private void Publish(DownloadItem item)
    {
        try
        {
            DownloadUpdated?.Invoke(this, item.ToSnapshot());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A DownloadUpdated handler failed");
        }
    }

    private void SaveInBackground(DownloadItem item) => _ = SaveAsync(item);

    private async Task SaveAsync(DownloadItem item)
    {
        try
        {
            await _repository.SaveAsync(item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save download {Id}", item.Id);
        }
    }

    private async Task PersistPositionsAsync(IReadOnlyList<(Guid, long)> positions)
    {
        try
        {
            await _repository.UpdateQueuePositionsAsync(positions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save queue order");
        }
    }

    private async Task RecordEventAsync(Guid id, string type, string? detail)
    {
        try
        {
            await _repository.AddEventAsync(new DownloadEvent(id, _time.GetUtcNow(), type, detail)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record event for {Id}", id);
        }
    }

    private async Task WithGateAsync(Action action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Secrets for one download; kept in memory and optionally in Windows Credential Manager.</summary>
    internal sealed record SessionSecrets(Dictionary<string, string>? Headers, string? UserName, string? Password);
}
