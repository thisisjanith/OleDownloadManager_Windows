using Microsoft.Extensions.Logging.Abstractions;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Engine;
using OleDownloadManager.Infrastructure.FileSystem;
using OleDownloadManager.Infrastructure.Media;
using OleDownloadManager.IntegrationTests.Infrastructure;

namespace OleDownloadManager.IntegrationTests;

/// <summary>
/// Exercises the optional yt-dlp integration against direct media files on the local test server
/// (yt-dlp's generic extractor). Skipped when yt-dlp is not installed. No public sites are contacted.
/// </summary>
public sealed class MediaToolTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private EngineHarness _harness = null!;
    private YtDlpMediaTool _tool = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await TestHttpServer.StartAsync();
        _harness = await EngineHarness.CreateAsync();
        _tool = new YtDlpMediaTool(_harness.Settings, NullLogger<YtDlpMediaTool>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await _server.DisposeAsync();
    }

    private async Task RequireToolAsync()
    {
        var status = await _tool.GetStatusAsync();
        Assert.SkipUnless(status.IsAvailable, "yt-dlp is not installed: " + status.Message);
    }

    [Fact]
    public async Task InspectsDirectMediaUrl()
    {
        await RequireToolAsync();
        var inspection = await _tool.InspectAsync(_server.Url("media/200000/clip.mp4"), TestContext.Current.CancellationToken);

        Assert.False(inspection.IsPlaylist);
        Assert.Single(inspection.Entries);
    }

    [Fact]
    public async Task DownloadsWithProgressIntoOutputFolder()
    {
        await RequireToolAsync();
        var output = Directory.CreateDirectory(Path.Combine(_harness.Root, "media-out")).FullName;
        var reports = new List<MediaProgress>();
        var progress = new SyncProgress(reports.Add);

        var path = await _tool.DownloadAsync(_server.Url("media/400000/clip.mp4?bps=400000"), output, "My Clip", "b", null, progress, TestContext.Current.CancellationToken);

        Assert.Equal(output, Path.GetDirectoryName(path));
        Assert.StartsWith("My Clip", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.Equal(TestContent.Sha256(400000), TestContent.FileSha256(path));
        Assert.NotEmpty(reports);
        Assert.Equal(400000, reports.Max(r => r.DownloadedBytes));
    }

    [Fact]
    public async Task MediaDownloadThroughCoordinatorCompletes()
    {
        await RequireToolAsync();
        var coordinator = new DownloadCoordinator(HttpRequestSender.CreateClient(HttpRequestSender.CreateHandler(TimeSpan.FromSeconds(10))),
            _harness.Repository, _harness.Settings, new FileSystemService(NullLogger<FileSystemService>.Instance), _harness.Credentials,
            _harness.Notifications, NullLoggerFactory.Instance, EngineHarness.FastOptions(), mediaTool: _tool);
        await coordinator.InitializeAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await coordinator.AddAsync(new()
            {
                Url = _server.Url("media/250000/song.mp3"),
                Kind = DownloadKind.Media,
                FileName = "Song Title",
                MediaFormat = "b",
                StartNow = true,
                DestinationDirectory = _harness.DownloadDirectory,
            }, TestContext.Current.CancellationToken);
            Assert.True(result.Accepted, result.Error);

            var deadline = DateTime.UtcNow.AddSeconds(60);
            Core.Models.DownloadSnapshot? snapshot;
            do
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                snapshot = coordinator.Get(result.DownloadId!.Value);
            }
            while (snapshot!.Status is not (DownloadStatus.Completed or DownloadStatus.Failed) && DateTime.UtcNow < deadline);

            Assert.Equal(DownloadStatus.Completed, snapshot.Status);
            Assert.Equal("Song Title.mp3", snapshot.FileName);
            Assert.Equal(TestContent.Sha256(250000), TestContent.FileSha256(snapshot.DestinationPath));
            Assert.False(Directory.Exists(Path.Combine(_harness.DownloadDirectory, FileSystemService.PartialDirectoryName)));
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    private sealed class SyncProgress(Action<MediaProgress> action) : IProgress<MediaProgress>
    {
        public void Report(MediaProgress value) => action(value);
    }
}
