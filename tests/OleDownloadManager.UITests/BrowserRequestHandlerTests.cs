using Microsoft.Extensions.Logging.Abstractions;
using OleDownloadManager.App.Services;
using OleDownloadManager.Core.Protocol;
using OleDownloadManager.IntegrationTests.Infrastructure;

namespace OleDownloadManager.UITests;

/// <summary>Protection against a browser flooding the app with links (e.g. replaying its download history).</summary>
public sealed class BrowserRequestHandlerTests : IAsyncLifetime
{
    private EngineHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await EngineHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task FloodOfBrowserLinks_IsCapped_AndDialogsAreShownOneAtATime()
    {
        var handler = new BrowserRequestHandler(_harness.Coordinator, _harness.Settings, NullLogger<BrowserRequestHandler>.Instance);
        var open = 0;
        var maxOpen = 0;
        var shown = 0;
        handler.ShowAddDialog = async _ =>
        {
            var now = Interlocked.Increment(ref open);
            InterlockedMax(ref maxOpen, now);
            await Task.Delay(30);
            Interlocked.Increment(ref shown);
            Interlocked.Decrement(ref open);
        };

        var replies = await Task.WhenAll(Enumerable.Range(0, 15).Select(i => handler.HandleAsync(
            new BrowserMessage { Type = MessageTypes.AddDownload, Version = 1, Url = $"https://example.com/old-{i}.zip" },
            TestContext.Current.CancellationToken)));

        Assert.Equal(BrowserRequestHandler.BurstLimit, replies.Count(r => r.Type == MessageTypes.DownloadAccepted));
        Assert.Equal(15 - BrowserRequestHandler.BurstLimit, replies.Count(r => r.Type == MessageTypes.DownloadRejected));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref shown) < BrowserRequestHandler.BurstLimit && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal(BrowserRequestHandler.BurstLimit, shown);
        Assert.Equal(1, maxOpen);
        Assert.Empty(_harness.Coordinator.GetAll());
    }

    [Fact]
    public void BurstLimitResetsAfterTheWindow()
    {
        var handler = new BrowserRequestHandler(_harness.Coordinator, _harness.Settings, NullLogger<BrowserRequestHandler>.Instance);
        var start = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < BrowserRequestHandler.BurstLimit; i++)
        {
            Assert.True(handler.TryAdmitBurst(start.AddMilliseconds(i)));
        }

        Assert.False(handler.TryAdmitBurst(start.AddSeconds(1)));
        Assert.True(handler.TryAdmitBurst(start + BrowserRequestHandler.BurstWindow + TimeSpan.FromSeconds(1)));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
