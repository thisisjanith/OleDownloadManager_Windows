using System.Collections.Concurrent;
using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OleDownloadManager.IntegrationTests.Infrastructure;

/// <summary>Deterministic, position-dependent test content so misplaced bytes are always detected.</summary>
public static class TestContent
{
    public static byte ByteAt(long position, int seed)
    {
        var x = ((ulong)position * 0x9E3779B97F4A7C15UL) + ((ulong)(uint)seed * 0xBF58476D1CE4E5B9UL);
        x ^= x >> 31;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 29;
        return (byte)x;
    }

    /// <summary>A 1 MiB block repeated by the /fast endpoint, so large transfers are not limited by content generation.</summary>
    public static readonly byte[] FastBlock = Create(1024 * 1024, 7);

    public static void FillFast(Span<byte> buffer, long offset)
    {
        for (var i = 0; i < buffer.Length;)
        {
            var position = (int)((offset + i) % FastBlock.Length);
            var count = Math.Min(buffer.Length - i, FastBlock.Length - position);
            FastBlock.AsSpan(position, count).CopyTo(buffer[i..]);
            i += count;
        }
    }

    public static string FastSha256(long size)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        for (long done = 0; done < size; done += FastBlock.Length)
        {
            hash.AppendData(FastBlock, 0, (int)Math.Min(FastBlock.Length, size - done));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static void Fill(Span<byte> buffer, long offset, int seed)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = ByteAt(offset + i, seed);
        }
    }

    public static byte[] Create(long size, int seed = 1)
    {
        var data = new byte[size];
        Fill(data, 0, seed);
        return data;
    }

    public static string Sha256(long size, int seed = 1) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Create(size, seed)));

    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }
}

/// <summary>
/// Local Kestrel server with endpoints that reproduce real-world server behaviour: range support,
/// ignored ranges, bad Content-Range, redirects, failures, throttling, aborted connections and
/// changing representations. Never depends on the public internet.
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _concurrentBodies;
    private int _maxConcurrentBodies;
    private long _bytesServed;

    private TestHttpServer(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }
    public ConcurrentDictionary<string, int> RequestCounts { get; } = new();
    public ConcurrentDictionary<string, int> Versions { get; } = new();
    public ConcurrentQueue<string> RangeLog { get; } = new();
    public int MaxConcurrentBodies => Volatile.Read(ref _maxConcurrentBodies);
    public long BytesServed => Interlocked.Read(ref _bytesServed);

    public Uri Url(string pathAndQuery) => new(BaseUri, pathAndQuery);

    public void ResetConcurrency() => Interlocked.Exchange(ref _maxConcurrentBodies, 0);

    public static async Task<TestHttpServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        TestHttpServer? server = null;

        app.MapGet("/file/{size:long}", (HttpContext c, long size) => server!.ServeAsync(c, size, Options.From(c)));
        app.MapGet("/fast/{size:long}", (HttpContext c, long size) => server!.ServeAsync(c, size, Options.From(c) with { Fast = true }));
        app.MapGet("/norange/{size:long}", (HttpContext c, long size) => server!.ServeAsync(c, size, Options.From(c) with { Ranges = false }));
        app.MapGet("/badrange/{size:long}", (HttpContext c, long size) => server!.ServeAsync(c, size, Options.From(c) with { BadRange = true }));
        app.MapGet("/unknown/{size:long}", (HttpContext c, long size) => server!.ServeAsync(c, size, Options.From(c) with { Ranges = false, UnknownLength = true }));
        app.MapGet("/redirect/{size:long}", (HttpContext c, long size) =>
        {
            c.Response.StatusCode = 302;
            c.Response.Headers.Location = $"/file/{size}{c.Request.QueryString}";
            return Task.CompletedTask;
        });
        app.MapGet("/redirect-loop", (HttpContext c) =>
        {
            c.Response.StatusCode = 302;
            c.Response.Headers.Location = "/redirect-loop";
            return Task.CompletedTask;
        });
        app.MapGet("/status/{code:int}", (HttpContext c, int code) =>
        {
            c.Response.StatusCode = code;
            if (code == 429)
            {
                c.Response.Headers.RetryAfter = "1";
            }

            return Task.CompletedTask;
        });
        app.MapGet("/flaky/{key}/{size:long}", (HttpContext c, string key, long size) =>
        {
            var failures = int.Parse(c.Request.Query["failures"].FirstOrDefault() ?? "2", CultureInfo.InvariantCulture);
            var count = server!.RequestCounts.AddOrUpdate(key, 1, (_, n) => n + 1);
            if (count <= failures)
            {
                c.Response.StatusCode = 500;
                return Task.CompletedTask;
            }

            return server.ServeAsync(c, size, Options.From(c));
        });
        app.MapGet("/interrupt/{key}/{size:long}", (HttpContext c, string key, long size) =>
        {
            var isProbe = c.Request.Headers.Range.ToString() == "bytes=0-0";
            var count = isProbe ? int.MaxValue : server!.RequestCounts.AddOrUpdate(key, 1, (_, n) => n + 1);
            var options = Options.From(c);
            return server!.ServeAsync(c, size, count <= 2 ? options with { AbortAfterFraction = 0.5 } : options);
        });
        app.MapGet("/throttle429/{key}/{size:long}", (HttpContext c, string key, long size) =>
        {
            var isProbe = c.Request.Headers.Range.ToString() == "bytes=0-0";
            if (!isProbe && server!.RequestCounts.AddOrUpdate(key, 1, (_, n) => n + 1) <= 2)
            {
                c.Response.StatusCode = 429;
                c.Response.Headers.RetryAfter = "1";
                return Task.CompletedTask;
            }

            return server!.ServeAsync(c, size, Options.From(c));
        });
        app.MapGet("/changing/{key}/{size:long}", (HttpContext c, string key, long size) =>
        {
            var count = server!.RequestCounts.AddOrUpdate(key, 1, (_, n) => n + 1);
            if (c.Request.Query.TryGetValue("changeAfter", out var after) && count > int.Parse(after!, CultureInfo.InvariantCulture))
            {
                server.Versions[key] = 2;
            }

            var version = server.Versions.GetOrAdd(key, 1);
            return server.ServeAsync(c, size, Options.From(c) with { Seed = version, ETag = $"\"v{version}\"" });
        });
        app.MapGet("/disposition/{size:long}", (HttpContext c, long size) =>
        {
            var name = c.Request.Query["name"].FirstOrDefault() ?? "report.pdf";
            var star = c.Request.Query["star"].FirstOrDefault();
            c.Response.Headers.ContentDisposition = star is null
                ? $"attachment; filename=\"{name}\""
                : $"attachment; filename=\"fallback.bin\"; filename*=UTF-8''{Uri.EscapeDataString(star)}";
            return server!.ServeAsync(c, size, Options.From(c));
        });
        app.MapGet("/media/{size:long}/{name}", (HttpContext c, long size, string name) =>
        {
            c.Response.ContentType = name.EndsWith(".mp3", StringComparison.Ordinal) ? "audio/mpeg" : "video/mp4";
            return server!.ServeAsync(c, size, Options.From(c));
        });
        app.MapGet("/gzip/{size:long}", async (HttpContext c, long size) =>
        {
            using var compressed = new MemoryStream();
            await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                await gzip.WriteAsync(TestContent.Create(size));
            }

            c.Response.StatusCode = 200;
            c.Response.Headers.ContentEncoding = "gzip";
            c.Response.ContentLength = compressed.Length;
            await c.Response.Body.WriteAsync(compressed.ToArray());
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new TestHttpServer(app, new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal) + "/"));
        return server;
    }

    public sealed record Options
    {
        public bool Ranges { get; init; } = true;
        public bool BadRange { get; init; }
        public bool UnknownLength { get; init; }
        public int Seed { get; init; } = 1;
        public string? ETag { get; init; } = "\"v1\"";
        public long? BytesPerSecond { get; init; }
        public double? AbortAfterFraction { get; init; }
        public bool Fast { get; init; }

        public static Options From(HttpContext context)
        {
            var query = context.Request.Query;
            var options = new Options();
            if (query.TryGetValue("bps", out var bps))
            {
                options = options with { BytesPerSecond = long.Parse(bps!, CultureInfo.InvariantCulture) };
            }

            if (query.TryGetValue("seed", out var seed))
            {
                options = options with { Seed = int.Parse(seed!, CultureInfo.InvariantCulture) };
            }

            if (query.TryGetValue("noetag", out _))
            {
                options = options with { ETag = null };
            }

            return options;
        }
    }

    private async Task ServeAsync(HttpContext context, long size, Options options)
    {
        var request = context.Request;
        var response = context.Response;
        var rangeHeader = request.Headers.Range.ToString();
        RangeLog.Enqueue($"{request.Path} {rangeHeader}");

        if (options.ETag is not null)
        {
            response.Headers.ETag = options.ETag;
        }

        response.Headers.LastModified = "Wed, 01 Jan 2025 00:00:00 GMT";
        if (options.Ranges)
        {
            response.Headers.AcceptRanges = "bytes";
        }

        long start = 0;
        var end = size - 1;
        var partial = false;
        if (options.Ranges && TryParseRange(rangeHeader, out var first, out var last))
        {
            var ifRange = request.Headers.IfRange.ToString();
            var validatorMatches = string.IsNullOrEmpty(ifRange) || ifRange == options.ETag || ifRange == "Wed, 01 Jan 2025 00:00:00 GMT";
            if (validatorMatches)
            {
                if (first >= size)
                {
                    response.StatusCode = 416;
                    response.Headers.ContentRange = $"bytes */{size}";
                    return;
                }

                start = first;
                end = Math.Min(last ?? size - 1, size - 1);
                partial = true;
            }
        }

        if (partial)
        {
            response.StatusCode = 206;
            var shift = options.BadRange && !(start == 0 && end == 0) ? 1 : 0;
            response.Headers.ContentRange = $"bytes {start + shift}-{end}/{size}";
        }
        else
        {
            response.StatusCode = 200;
        }

        var length = Math.Max(0, end - start + 1);
        if (!options.UnknownLength)
        {
            response.ContentLength = length;
        }

        var current = Interlocked.Increment(ref _concurrentBodies);
        if (length > 1)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxConcurrentBodies);
            }
            while (current > observed && Interlocked.CompareExchange(ref _maxConcurrentBodies, current, observed) != observed);
        }

        try
        {
            var abortAt = options.AbortAfterFraction is { } fraction ? (long)(length * fraction) : long.MaxValue;
            var buffer = new byte[options.Fast ? 256 * 1024 : 16 * 1024];
            long sent = 0;
            while (sent < length)
            {
                var chunk = (int)Math.Min(buffer.Length, length - sent);
                if (options.BytesPerSecond is { } bps)
                {
                    chunk = (int)Math.Max(1, Math.Min(chunk, bps / 20));
                }

                if (sent + chunk > abortAt)
                {
                    chunk = (int)Math.Max(0, abortAt - sent);
                    if (chunk > 0)
                    {
                        TestContent.Fill(buffer.AsSpan(0, chunk), start + sent, options.Seed);
                        await response.Body.WriteAsync(buffer.AsMemory(0, chunk));
                        await response.Body.FlushAsync();
                    }

                    context.Abort();
                    return;
                }

                if (options.Fast)
                {
                    TestContent.FillFast(buffer.AsSpan(0, chunk), start + sent);
                }
                else
                {
                    TestContent.Fill(buffer.AsSpan(0, chunk), start + sent, options.Seed);
                }

                await response.Body.WriteAsync(buffer.AsMemory(0, chunk), context.RequestAborted);
                sent += chunk;
                Interlocked.Add(ref _bytesServed, chunk);
                if (options.BytesPerSecond is { } rate)
                {
                    await Task.Delay(TimeSpan.FromSeconds((double)chunk / rate), context.RequestAborted);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected (pause/cancel).
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentBodies);
        }
    }

    private static bool TryParseRange(string header, out long first, out long? last)
    {
        first = 0;
        last = null;
        if (!header.StartsWith("bytes=", StringComparison.Ordinal) || header.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = header["bytes=".Length..].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out first))
        {
            return false;
        }

        if (parts[1].Length > 0)
        {
            if (!long.TryParse(parts[1], CultureInfo.InvariantCulture, out var l) || l < first)
            {
                return false;
            }

            last = l;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
