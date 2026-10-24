using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Protocol;
using OleDownloadManager.Infrastructure.FileSystem;
using OleDownloadManager.Infrastructure.Media;
using OleDownloadManager.Infrastructure.Persistence;
using OleDownloadManager.Infrastructure.Settings;
using OleDownloadManager.Infrastructure.Windows;

namespace OleDownloadManager.UnitTests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "oledm-db-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private SqliteDatabase Database(string name = "t.db") => new(Path.Combine(_dir, name), NullLogger<SqliteDatabase>.Instance);

    [Fact]
    public async Task MigrationsCreateLatestSchema_AndAreIdempotent()
    {
        var db = Database();
        await db.EnsureMigratedAsync();
        Assert.Equal(SqliteDatabase.LatestVersion, db.GetSchemaVersion());

        var again = Database();
        await again.EnsureMigratedAsync();
        Assert.Equal(SqliteDatabase.LatestVersion, again.GetSchemaVersion());
    }

    [Fact]
    public async Task MigratesFromVersionOne()
    {
        var path = Path.Combine(_dir, "v1.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            foreach (var statement in SqliteDatabase.Migrations[0])
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version = 1";
            version.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var db = new SqliteDatabase(path, NullLogger<SqliteDatabase>.Instance);
        await db.EnsureMigratedAsync();
        Assert.Equal(2, db.GetSchemaVersion());
        var repo = new SqliteDownloadRepository(db);
        await repo.AddEventAsync(new DownloadEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "Test", null));
    }

    [Fact]
    public async Task RefusesNewerSchema()
    {
        var path = Path.Combine(_dir, "future.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version = 999";
            version.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var db = new SqliteDatabase(path, NullLogger<SqliteDatabase>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.EnsureMigratedAsync());
    }

    [Fact]
    public async Task RoundTripsDownloadWithSegments()
    {
        var repo = new SqliteDownloadRepository(Database());
        await repo.InitializeAsync();
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            SourceUrl = new Uri("https://example.com/a b.zip?x=1"),
            FinalUrl = new Uri("https://cdn.example.com/a.zip"),
            FileName = "ünïcödé.zip",
            DestinationDirectory = @"D:\Downloads",
            TemporaryDirectory = @"D:\Downloads\.oledm-partial\abc",
            ExpectedSize = 300,
            Status = DownloadStatus.Paused,
            Priority = DownloadPriority.High,
            Category = DownloadCategory.Archives,
            CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5)),
            ScheduledAt = DateTimeOffset.UnixEpoch,
            ETag = "\"abc\"",
            LastModified = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            SupportsRangeRequests = true,
            SpeedLimit = 1000,
            Headers = new Dictionary<string, string> { ["X-Test"] = "1" },
            LastErrorKind = DownloadErrorKind.ServerError,
        };
        item.Segments.Add(new DownloadSegment { Index = 0, StartOffset = 0, EndOffset = 149, DownloadedBytes = 149, DurableBytes = 100 });
        item.Segments.Add(new DownloadSegment { Index = 1, StartOffset = 150, EndOffset = 299, DownloadedBytes = 150, DurableBytes = 150 });

        await repo.SaveAsync(item);
        var loaded = (await repo.LoadAllAsync()).Single();

        Assert.Equal(item.SourceUrl, loaded.SourceUrl);
        Assert.Equal(item.FinalUrl, loaded.FinalUrl);
        Assert.Equal(item.FileName, loaded.FileName);
        Assert.Equal(item.CreatedAt, loaded.CreatedAt);
        Assert.Equal(item.CreatedAt.Offset, loaded.CreatedAt.Offset);
        Assert.Equal(item.ScheduledAt, loaded.ScheduledAt);
        Assert.Equal(item.ETag, loaded.ETag);
        Assert.Equal(item.LastModified, loaded.LastModified);
        Assert.True(loaded.SupportsRangeRequests);
        Assert.Equal(DownloadPriority.High, loaded.Priority);
        Assert.Equal(DownloadErrorKind.ServerError, loaded.LastErrorKind);
        Assert.Equal("1", loaded.Headers["x-test"]);
        Assert.Equal(2, loaded.Segments.Count);
        // Only durable bytes are persisted.
        Assert.Equal(100, loaded.Segments[0].DownloadedBytes);
        Assert.Equal(250, loaded.DownloadedBytes);
        Assert.Equal(SegmentState.Completed, loaded.Segments[1].State);
    }

    [Fact]
    public async Task SaveReplacesSegmentsAndUpdatesFields()
    {
        var repo = new SqliteDownloadRepository(Database());
        await repo.InitializeAsync();
        var item = new DownloadItem { Id = Guid.NewGuid(), SourceUrl = new Uri("https://e.com/f"), FileName = "f", DestinationDirectory = @"C:\d", CreatedAt = DateTimeOffset.UtcNow };
        item.Segments.Add(new DownloadSegment { Index = 0, StartOffset = 0, EndOffset = 9 });
        await repo.SaveAsync(item);

        item.Segments.Clear();
        item.Status = DownloadStatus.Completed;
        item.FileName = "renamed";
        await repo.SaveAsync(item);

        var loaded = (await repo.LoadAllAsync()).Single();
        Assert.Empty(loaded.Segments);
        Assert.Equal("renamed", loaded.FileName);
        Assert.Equal(DownloadStatus.Completed, loaded.Status);
    }

    [Fact]
    public async Task ConcurrentSavesDoNotFail()
    {
        var repo = new SqliteDownloadRepository(Database());
        await repo.InitializeAsync();
        var items = Enumerable.Range(0, 20).Select(i => new DownloadItem
        {
            Id = Guid.NewGuid(), SourceUrl = new Uri($"https://e.com/{i}"), FileName = $"{i}", DestinationDirectory = @"C:\d", CreatedAt = DateTimeOffset.UtcNow,
        }).ToList();

        await Task.WhenAll(items.SelectMany(i => Enumerable.Range(0, 5).Select(_ => repo.SaveAsync(i))));
        await repo.UpdateQueuePositionsAsync(items.Select((i, n) => (i.Id, (long)(100 - n))).ToList());

        var loaded = await repo.LoadAllAsync();
        Assert.Equal(20, loaded.Count);
        Assert.Equal(items[^1].Id, loaded[0].Id);
    }

    [Fact]
    public async Task DeleteRemovesSegmentsAndEvents()
    {
        var repo = new SqliteDownloadRepository(Database());
        await repo.InitializeAsync();
        var item = new DownloadItem { Id = Guid.NewGuid(), SourceUrl = new Uri("https://e.com/f"), FileName = "f", DestinationDirectory = @"C:\d", CreatedAt = DateTimeOffset.UtcNow };
        item.Segments.Add(new DownloadSegment { Index = 0, StartOffset = 0, EndOffset = 9 });
        await repo.SaveAsync(item);
        await repo.AddEventAsync(new DownloadEvent(item.Id, DateTimeOffset.UtcNow, "Added", null));
        Assert.Single(await repo.GetEventsAsync(item.Id));

        await repo.DeleteAsync(item.Id);
        Assert.Empty(await repo.LoadAllAsync());
        Assert.Empty(await repo.GetEventsAsync(item.Id));
    }

    [Fact]
    public async Task SettingsRoundTripAndRaiseChange()
    {
        var db = Database();
        var store = new SqliteSettingsStore(db, NullLogger<SqliteSettingsStore>.Instance);
        await store.LoadAsync();
        AppSettings? changed = null;
        store.SettingsChanged += (_, s) => changed = s;

        await store.UpdateAsync(s =>
        {
            s.MaxActiveDownloads = 99;
            s.Theme = AppTheme.Dark;
            s.GlobalSpeedLimit = 1234;
            s.QueueDailyStart = new TimeOnly(22, 30);
            s.CategoryFolders[DownloadCategory.Video] = @"E:\Videos";
        });

        var reloaded = new SqliteSettingsStore(db, NullLogger<SqliteSettingsStore>.Instance);
        await reloaded.LoadAsync();
        Assert.Equal(20, reloaded.Current.MaxActiveDownloads);
        Assert.Equal(AppTheme.Dark, reloaded.Current.Theme);
        Assert.Equal(1234, reloaded.Current.GlobalSpeedLimit);
        Assert.Equal(new TimeOnly(22, 30), reloaded.Current.QueueDailyStart);
        Assert.Equal(@"E:\Videos", reloaded.Current.CategoryFolders[DownloadCategory.Video]);
        Assert.NotNull(changed);
    }
}

public sealed class FileSystemServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "oledm-fs-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    private readonly FileSystemService _fs = new(NullLogger<FileSystemService>.Instance);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ResolveConflict_RenamesSkipsOrReplaces()
    {
        File.WriteAllText(Path.Combine(_dir, "a.zip"), "x");
        File.WriteAllText(Path.Combine(_dir, "a (1).zip"), "x");

        Assert.Equal("new.zip", _fs.ResolveConflict(_dir, "new.zip", ConflictPolicy.Rename));
        Assert.Equal("a (2).zip", _fs.ResolveConflict(_dir, "a.zip", ConflictPolicy.Rename));
        Assert.Null(_fs.ResolveConflict(_dir, "a.zip", ConflictPolicy.Skip));
        Assert.Equal("a.zip", _fs.ResolveConflict(_dir, "a.zip", ConflictPolicy.Replace));
    }

    [Fact]
    public void ResolveConflict_TreatsReservedPathsAsTaken()
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(_dir, "b.bin") };
        Assert.Equal("b (1).bin", _fs.ResolveConflict(_dir, "b.bin", ConflictPolicy.Replace, reserved));
        Assert.Equal("B (1).BIN", _fs.ResolveConflict(_dir, "B.BIN", ConflictPolicy.Skip, reserved));
    }

    [Fact]
    public void ResolveConflict_SanitizesTraversal()
    {
        Assert.Equal("evil.exe", _fs.ResolveConflict(_dir, @"..\..\evil.exe", ConflictPolicy.Rename));
    }

    [Fact]
    public void SafeCombineRejectsEscapes()
    {
        Assert.Throws<InvalidOperationException>(() => FileSystemService.SafeCombine(_dir, @"..\x.txt"));
        Assert.Equal(Path.Combine(_dir, "x.txt"), FileSystemService.SafeCombine(_dir, "x.txt"));
    }

    [Fact]
    public void FitToPathKeepsFullPathUnderMaxPath()
    {
        var name = new string('n', 300) + ".txt";
        var fitted = FileSystemService.FitToPath(_dir, name);
        Assert.True(Path.Combine(_dir, fitted).Length <= 259);
        Assert.EndsWith(".txt", fitted, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckDestination_ReportsWritableAndFreeSpace()
    {
        var result = _fs.CheckDestination(Path.Combine(_dir, "new-sub"), 1);
        Assert.True(result.IsUsable);
        Assert.True(result.FreeBytes > 0);
        Assert.True(Directory.Exists(Path.Combine(_dir, "new-sub")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "new-sub")));
    }

    [Fact]
    public void CheckDestination_ReportsDiskFull()
    {
        var result = _fs.CheckDestination(_dir, long.MaxValue / 2);
        Assert.False(result.IsUsable);
        Assert.Equal(DownloadErrorKind.DiskFull, result.ErrorKind);
    }

    [Fact]
    public void CheckDestination_ReportsUnavailableDrive()
    {
        var unused = Enumerable.Range('G', 20).Select(c => (char)c).First(c => !Directory.Exists($"{c}:\\"));
        var result = _fs.CheckDestination($"{unused}:\\Downloads", null);
        Assert.False(result.IsUsable);
        Assert.Equal(DownloadErrorKind.DestinationUnavailable, result.ErrorKind);
    }

    [Theory]
    [InlineData(0L, 0L, true)]
    [InlineData(100L, DiskSpace.SafetyMargin + 100, true)]
    [InlineData(101L, DiskSpace.SafetyMargin + 100, false)]
    [InlineData(1L, 10L, false)]
    public void DiskSpaceIncludesSafetyMargin(long required, long available, bool expected)
    {
        Assert.Equal(expected, DiskSpace.HasEnoughSpace(required, available));
    }

    [Fact]
    public void TemporaryDirectoryIsHiddenAndDeletionIsGuarded()
    {
        var temp = _fs.EnsureTemporaryDirectory(_dir, Guid.NewGuid());
        Assert.True(new DirectoryInfo(Path.GetDirectoryName(temp)!).Attributes.HasFlag(FileAttributes.Hidden));
        File.WriteAllText(Path.Combine(temp, "seg-000.part"), "x");

        var outsider = Directory.CreateDirectory(Path.Combine(_dir, "keep")).FullName;
        _fs.DeleteTemporaryData(outsider);
        Assert.True(Directory.Exists(outsider));

        _fs.DeleteTemporaryData(temp);
        Assert.False(Directory.Exists(temp));
        Assert.False(Directory.Exists(Path.GetDirectoryName(temp)));
    }

    [Fact]
    public void FinalizeNeverOverwritesUnlessAsked()
    {
        var source = Path.Combine(_dir, "src.part");
        var target = Path.Combine(_dir, "target.bin");
        File.WriteAllText(source, "new");
        File.WriteAllText(target, "old");

        Assert.Throws<IOException>(() => _fs.FinalizeFile(source, target, replaceExisting: false));
        Assert.Equal("old", File.ReadAllText(target));
        _fs.FinalizeFile(source, target, replaceExisting: true);
        Assert.Equal("new", File.ReadAllText(target));
    }

    [Fact]
    public void WritesMarkOfTheWeb()
    {
        var file = Path.Combine(_dir, "motw.exe");
        File.WriteAllText(file, "x");
        _fs.ApplyMarkOfTheWeb(file, new Uri("https://example.com/dl/motw.exe?token=secret"), new Uri("https://example.com/page?q=1"));

        var zone = File.ReadAllText(file + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", zone, StringComparison.Ordinal);
        Assert.Contains("HostUrl=https://example.com/dl/motw.exe", zone, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", zone, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultDownloadDirectoryExists()
    {
        Assert.False(string.IsNullOrWhiteSpace(_fs.GetDefaultDownloadDirectory()));
    }
}

public sealed class StartupRegistrationTests
{
    [Fact]
    public void CreatesAShortcutWithTargetAndArguments()
    {
        // Uses a temporary path; the real Startup folder is never touched by tests.
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "oledm-lnk-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var shortcut = Path.Combine(directory, StartupRegistration.ShortcutName);
            StartupRegistration.CreateShortcut(shortcut, target, "--background", "test");

            Assert.True(File.Exists(shortcut));
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(shortcut);
            Assert.Equal(target, (string)link.TargetPath, ignoreCase: true);
            Assert.Equal("--background", (string)link.Arguments);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}

public sealed class BrowserProtocolTests
{
    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void AcceptsValidAddDownload()
    {
        Assert.True(BrowserMessageValidator.TryParse(Json("""{"type":"addDownload","version":1,"requestId":"r-1","url":"https://e.com/a.zip","pageUrl":"https://e.com/","fileName":"../a.zip"}"""),
            MessageTypes.FromExtension, out var message, out var error), error);
        Assert.Equal("a.zip", message!.FileName);
        Assert.Equal("https://e.com/a.zip", message.Url);
    }

    [Theory]
    [InlineData("""{"type":"addDownload","version":1,"url":"file:///C:/x"}""")]
    [InlineData("""{"type":"addDownload","version":1,"url":"javascript:alert(1)"}""")]
    [InlineData("""{"type":"addDownload","version":1,"url":"example.com/a"}""")]
    [InlineData("""{"type":"addDownload","version":1}""")]
    [InlineData("""{"type":"activate","version":1}""")]
    [InlineData("""{"type":"shutdown","version":1}""")]
    [InlineData("""{"type":"hello","version":99}""")]
    [InlineData("""{"type":"hello"}""")]
    [InlineData("""{"type":"hello","version":1,"requestId":"bad id with spaces"}""")]
    [InlineData("""not json""")]
    [InlineData("""[]""")]
    public void RejectsInvalidMessages(string json)
    {
        Assert.False(BrowserMessageValidator.TryParse(Json(json), MessageTypes.FromExtension, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void RejectsOversizedAndDeeplyNestedPayloads()
    {
        var huge = Json("""{"type":"hello","version":1,"reason":" """ + new string('x', 70_000) + "\"}");
        Assert.False(BrowserMessageValidator.TryParse(huge, MessageTypes.FromExtension, out _, out _));
        var nested = Json("{\"type\":\"hello\",\"version\":1,\"x\":" + new string('[', 100) + new string(']', 100) + "}");
        Assert.False(BrowserMessageValidator.TryParse(nested, MessageTypes.FromExtension, out _, out _));
    }

    [Fact]
    public void DropsInvalidPageUrlButKeepsDownload()
    {
        Assert.True(BrowserMessageValidator.TryParse(Json("""{"type":"addDownload","version":1,"url":"https://e.com/a","pageUrl":"chrome://settings"}"""),
            MessageTypes.FromExtension, out var message, out _));
        Assert.Null(message!.PageUrl);
    }

    [Fact]
    public async Task FramingRoundTrips()
    {
        using var stream = new MemoryStream();
        await MessageFraming.WriteAsync(stream, new BrowserMessage { Type = "hello", Version = 1 }, CancellationToken.None);
        Assert.Equal(4 + BrowserMessageValidator.Serialize(new BrowserMessage { Type = "hello", Version = 1 }).Length, stream.Length);
        stream.Position = 0;
        var payload = await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None);
        Assert.Contains("\"hello\"", Encoding.UTF8.GetString(payload!), StringComparison.Ordinal);
        Assert.Null(await MessageFraming.ReadAsync(stream, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task FramingRejectsBadLengths()
    {
        using var negative = new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF]);
        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.ReadAsync(negative, 1024, CancellationToken.None));
        using var tooLarge = new MemoryStream([0x00, 0x10, 0x00, 0x00]);
        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.ReadAsync(tooLarge, 1024, CancellationToken.None));
        using var truncated = new MemoryStream([0x05, 0x00, 0x00, 0x00, 0x7B]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => MessageFraming.ReadAsync(truncated, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task LocalPipeRoundTrip()
    {
        var name = LocalPipe.BuildPipeName("unit-" + Guid.NewGuid());
        await using var server = new LocalPipeServer((m, _) => Task.FromResult(new BrowserMessage { Type = MessageTypes.Status, Version = 1, RequestId = m.RequestId, ActiveDownloads = 7 }), pipeName: name);
        server.Start();

        var reply = await LocalPipe.SendAsync(new BrowserMessage { Type = MessageTypes.GetStatus, Version = 1, RequestId = "abc" }, TimeSpan.FromSeconds(5), CancellationToken.None, name);
        Assert.Equal(7, reply!.ActiveDownloads);
        Assert.Equal("abc", reply.RequestId);

        var rejected = await LocalPipe.SendAsync(new BrowserMessage { Type = "bogus", Version = 1 }, TimeSpan.FromSeconds(5), CancellationToken.None, name);
        Assert.Equal(MessageTypes.Error, rejected!.Type);
    }

    [Fact]
    public async Task LocalPipeReturnsNullWhenNoServer()
    {
        var reply = await LocalPipe.SendAsync(new BrowserMessage { Type = "hello", Version = 1 }, TimeSpan.FromMilliseconds(200), CancellationToken.None, LocalPipe.BuildPipeName("none-" + Guid.NewGuid()));
        Assert.Null(reply);
    }

    [Fact]
    public void ManifestsListOnlyExplicitExtensions()
    {
        var chrome = NativeMessagingRegistration.BuildChromeManifest(@"C:\app\host.exe", ["abcdefghijklmnopabcdefghijklmnop"]);
        Assert.Contains("chrome-extension://abcdefghijklmnopabcdefghijklmnop/", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("*", chrome, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => NativeMessagingRegistration.BuildChromeManifest("x", ["*"]));
        Assert.Throws<ArgumentException>(() => NativeMessagingRegistration.BuildFirefoxManifest("x", ["*"]));
        Assert.Contains("allowed_extensions", NativeMessagingRegistration.BuildFirefoxManifest("x", ["a@b"]), StringComparison.Ordinal);
    }
}

public sealed class MediaToolTests
{
    [Fact]
    public void ParsesPlaylistInspection()
    {
        const string json = """
            {"_type":"playlist","title":"My list","entries":[
              {"id":"a1","title":"First","url":"https://www.example.com/watch?v=a1","duration":61.5},
              {"id":"b2","title":"Second","url":"javascript:alert(1)"},
              {"id":"c3","title":"Third","webpage_url":"https://www.example.com/watch?v=c3"}
            ]}
            """;
        var result = YtDlpMediaTool.ParseInspection(json, new Uri("https://www.example.com/playlist?list=x"));
        Assert.True(result.IsPlaylist);
        Assert.Equal("My list", result.Title);
        Assert.Equal(["a1", "c3"], result.Entries.Select(e => e.Id));
        Assert.Equal(TimeSpan.FromSeconds(61.5), result.Entries[0].Duration);
    }

    [Fact]
    public void ParsesSingleVideoInspection()
    {
        var result = YtDlpMediaTool.ParseInspection("""{"id":"v","title":"Clip","webpage_url":"https://e.com/v"}""", new Uri("https://e.com/v?x"));
        Assert.False(result.IsPlaylist);
        Assert.Single(result.Entries);
        Assert.Equal("Clip", result.Entries[0].Title);
    }

    [Fact]
    public void ProgressTrackerAccumulatesStreams()
    {
        var reports = new List<MediaProgress>();
        var tracker = new YtDlpMediaTool.ProgressTracker(new SyncProgress(reports.Add));
        tracker.Report("500000|1000000|NA|1000.5");
        tracker.Report("1000000|1000000|NA|NA");
        tracker.Report("1000|200000|NA|500");
        tracker.Report("NA|NA|300000.0|NA");

        Assert.Equal(500_000, reports[0].DownloadedBytes);
        Assert.Equal(1_000_000, reports[0].TotalBytes);
        Assert.Equal(1_001_000, reports[2].DownloadedBytes);
        Assert.Equal(1_200_000, reports[2].TotalBytes);
        Assert.Equal(1000.5, reports[0].BytesPerSecond);
    }

    private sealed class SyncProgress(Action<MediaProgress> action) : IProgress<MediaProgress>
    {
        public void Report(MediaProgress value) => action(value);
    }
}
