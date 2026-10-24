using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Engine;
using OleDownloadManager.Infrastructure.FileSystem;
using OleDownloadManager.Infrastructure.Persistence;
using OleDownloadManager.Infrastructure.Settings;

namespace OleDownloadManager.IntegrationTests.Infrastructure;

public sealed class InMemoryCredentialStore : ICredentialStore
{
    public ConcurrentDictionary<string, string> Secrets { get; } = new();

    public void Save(string key, string secret) => Secrets[key] = secret;

    public string? Read(string key) => Secrets.GetValueOrDefault(key);

    public void Delete(string key) => Secrets.TryRemove(key, out _);
}

public sealed class RecordingNotifications : INotificationService
{
    public ConcurrentQueue<(string Title, string Message, NotificationKind Kind)> Items { get; } = new();

    public void Notify(string title, string message, NotificationKind kind) => Items.Enqueue((title, message, kind));
}

/// <summary>
/// A real coordinator wired to real SQLite, file system and HTTP, using a private temp folder.
/// Restart() disposes the coordinator and builds a new one on the same database, like an app restart.
/// </summary>
public sealed class EngineHarness : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<DownloadSnapshot>> _history = new();

    private EngineHarness(string root, Action<AppSettings>? configure, EngineOptions options)
    {
        Root = root;
        DownloadDirectory = Path.Combine(root, "downloads");
        Directory.CreateDirectory(DownloadDirectory);
        Options = options;
        _httpClient = HttpRequestSender.CreateClient(HttpRequestSender.CreateHandler(TimeSpan.FromSeconds(10)));
        Configure = configure;
    }

    public string Root { get; }
    public string DownloadDirectory { get; }
    public EngineOptions Options { get; }
    public Action<AppSettings>? Configure { get; }
    public DownloadCoordinator Coordinator { get; private set; } = null!;
    public SqliteDownloadRepository Repository { get; private set; } = null!;
    public SqliteSettingsStore Settings { get; private set; } = null!;
    public InMemoryCredentialStore Credentials { get; } = new();
    public RecordingNotifications Notifications { get; } = new();

    public static EngineOptions FastOptions() => new()
    {
        MinimumSegmentSize = 128 * 1024,
        BufferSize = 16 * 1024,
        ProgressInterval = TimeSpan.FromMilliseconds(50),
        CheckpointInterval = TimeSpan.FromMilliseconds(200),
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        MaintenanceInterval = TimeSpan.FromMilliseconds(200),
        ResponseHeadersTimeout = TimeSpan.FromSeconds(10),
        ReadInactivityTimeout = TimeSpan.FromSeconds(10),
    };

    public static async Task<EngineHarness> CreateAsync(Action<AppSettings>? configure = null, EngineOptions? options = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "oledm-it-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        var harness = new EngineHarness(root, configure, options ?? FastOptions());
        await harness.StartCoordinatorAsync(applyInitialSettings: true);
        return harness;
    }

    private async Task StartCoordinatorAsync(bool applyInitialSettings = false)
    {
        var database = new SqliteDatabase(Path.Combine(Root, "oledm.db"), NullLogger<SqliteDatabase>.Instance);
        Repository = new SqliteDownloadRepository(database);
        Settings = new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance);
        await Settings.LoadAsync();
        if (applyInitialSettings)
        {
            await Settings.UpdateAsync(s =>
            {
                s.DefaultDownloadDirectory = DownloadDirectory;
                s.MaxRetries = 3;
                s.NotificationsEnabled = true;
                Configure?.Invoke(s);
            });
        }

        Coordinator = new DownloadCoordinator(_httpClient, Repository, Settings, new FileSystemService(NullLogger<FileSystemService>.Instance),
            Credentials, Notifications, NullLoggerFactory.Instance, Options);
        Coordinator.DownloadUpdated += (_, snapshot) => _history.GetOrAdd(snapshot.Id, _ => new ConcurrentQueue<DownloadSnapshot>()).Enqueue(snapshot);
        await Coordinator.InitializeAsync();
    }

    /// <summary>Simulates closing and reopening the application.</summary>
    public async Task RestartAsync(Action<AppSettings>? changeSettings = null)
    {
        await Coordinator.DisposeAsync();
        SqliteConnectionPoolReset();
        await StartCoordinatorAsync();
        if (changeSettings is not null)
        {
            await Settings.UpdateAsync(changeSettings);
        }
    }

    public IReadOnlyList<DownloadSnapshot> History(Guid id) =>
        _history.TryGetValue(id, out var queue) ? queue.ToArray() : [];

    public async Task<Guid> AddAsync(Uri url, bool startNow = true, Func<DownloadRequest, DownloadRequest>? customize = null)
    {
        var request = new DownloadRequest { Url = url, StartNow = startNow };
        if (customize is not null)
        {
            request = customize(request);
        }

        var result = await Coordinator.AddAsync(request);
        Assert.True(result.Accepted, result.Error);
        return result.DownloadId!.Value;
    }

    public async Task<DownloadSnapshot> WaitForAsync(Guid id, Func<DownloadSnapshot, bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = Coordinator.Get(id);
            if (snapshot is not null && condition(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(25);
        }

        var last = Coordinator.Get(id);
        throw new TimeoutException($"Condition not met. Last state: {last?.Status} {last?.DownloadedBytes}/{last?.ExpectedSize} error={last?.LastError}");
    }

    public Task<DownloadSnapshot> WaitForStatusAsync(Guid id, DownloadStatus status, TimeSpan? timeout = null) =>
        WaitForAsync(id, s => s.Status == status, timeout);

    public Task<DownloadSnapshot> WaitForTerminalAsync(Guid id, TimeSpan? timeout = null) =>
        WaitForAsync(id, s => s.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled, timeout);

    private static void SqliteConnectionPoolReset() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync();
        _httpClient.Dispose();
        SqliteConnectionPoolReset();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
