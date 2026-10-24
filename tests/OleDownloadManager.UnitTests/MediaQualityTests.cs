using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Validation;
using OleDownloadManager.Infrastructure.Media;

namespace OleDownloadManager.UnitTests;

public sealed class MediaUrlDetectorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&list=PL123")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/abc123")]
    [InlineData("https://www.youtube.com/playlist?list=PL123")]
    [InlineData("https://www.youtube.com/live/abc123")]
    [InlineData("https://www.youtube-nocookie.com/embed/abc123")]
    [InlineData("https://vimeo.com/76979871")]
    [InlineData("https://www.dailymotion.com/video/x8abc")]
    public void RecognizesVideoPages(string url) => Assert.True(MediaUrlDetector.IsMediaPage(url));

    [Theory]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/feed/subscriptions")]
    [InlineData("https://youtu.be/")]
    [InlineData("https://example.com/watch?v=abc")]
    [InlineData("https://cdn.example.com/video.mp4")]
    [InlineData("https://vimeo.com/about")]
    [InlineData("ftp://www.youtube.com/watch?v=abc")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void IgnoresFilesAndOtherPages(string? url) => Assert.False(MediaUrlDetector.IsMediaPage(url));
}

public sealed class MediaQualityTests
{
    // Shape of yt-dlp's YouTube output: separate video-only and audio-only streams plus one combined 360p file.
    private const string YouTubeLikeJson = """
        {"id":"abc","title":"Clip","duration":100,"webpage_url":"https://www.youtube.com/watch?v=abc","formats":[
          {"format_id":"sb0","format_note":"storyboard","vcodec":"none","acodec":"none","height":45},
          {"format_id":"140","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2","tbr":129.5,"filesize":1600000},
          {"format_id":"251","ext":"webm","vcodec":"none","acodec":"opus","tbr":135.0,"filesize":1700000},
          {"format_id":"18","ext":"mp4","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360,"tbr":500,"filesize_approx":6000000},
          {"format_id":"134","ext":"mp4","vcodec":"avc1.4d401e","acodec":"none","height":360,"tbr":600,"filesize":7000000},
          {"format_id":"136","ext":"mp4","vcodec":"avc1.4d401f","acodec":"none","height":720,"tbr":2000,"filesize":25000000},
          {"format_id":"137","ext":"mp4","vcodec":"avc1.640028","acodec":"none","height":1080,"tbr":4000},
          {"format_id":"401","ext":"mp4","vcodec":"av01.0.12M.08","acodec":"none","height":2160,"tbr":15000,"filesize":190000000}
        ]}
        """;

    private static readonly Uri YouTube = new("https://www.youtube.com/watch?v=abc");

    [Fact]
    public void QualitiesComeFromTheVideosRealFormats()
    {
        var result = YtDlpMediaTool.ParseInspection(YouTubeLikeJson, YouTube, ffmpegAvailable: true);

        Assert.Equal(
            ["2160p (4K)", "1080p (Full HD)", "720p (HD)", "360p", "Audio only (original format)", "Audio only (MP3)"],
            result.Qualities.Select(q => q.Label));
        Assert.Equal(MediaFormats.UpTo(1080), result.Qualities[1].Selector);
        Assert.Equal(190_000_000 + 1_700_000, result.Qualities[0].ApproximateBytes);

        // No size reported for 1080p: estimated from bitrate x duration (4000 kbit/s * 100 s) plus best audio.
        Assert.Equal(50_000_000 + 1_700_000, result.Qualities[1].ApproximateBytes);
        Assert.True(result.Qualities[^1].AudioOnly);
    }

    [Fact]
    public void WithoutFfmpegOnlySingleFileQualitiesAreOffered()
    {
        var result = YtDlpMediaTool.ParseInspection(YouTubeLikeJson, YouTube, ffmpegAvailable: false);

        Assert.Equal(["360p", "Audio only (original format)"], result.Qualities.Select(q => q.Label));
        Assert.Equal(MediaFormats.SingleFileUpTo(360), result.Qualities[0].Selector);
        Assert.DoesNotContain(result.Qualities, q => MediaFormats.NeedsFfmpeg(q.Selector));
    }

    [Fact]
    public void SingleFileSitesWithoutCodecInfoAreHandled()
    {
        // archive.org style (captured from real yt-dlp output): no codec fields, every format is a complete file.
        const string json = """
            {"id":"bbb","title":"Big Buck Bunny","formats":[
              {"ext":"ogv","height":300,"filesize":46935223},
              {"ext":"mp4","height":360,"filesize":61878609},
              {"ext":"avi","height":720,"filesize":332243668}]}
            """;
        var result = YtDlpMediaTool.ParseInspection(json, new Uri("https://archive.org/details/x"));

        Assert.Equal(["720p (HD)", "360p", "300p"], result.Qualities.Where(q => !q.AudioOnly).Select(q => q.Label));
        Assert.Equal(332243668, result.Qualities[0].ApproximateBytes);
    }

    [Fact]
    public void PlaylistsGetStandardQualityCaps()
    {
        const string json = """{"_type":"playlist","title":"L","entries":[{"id":"a","url":"https://www.youtube.com/watch?v=a"}]}""";
        var result = YtDlpMediaTool.ParseInspection(json, new Uri("https://www.youtube.com/playlist?list=x"));

        Assert.Equal(MediaFormats.Best, result.Qualities[0].Selector);
        Assert.Contains(result.Qualities, q => q.Selector == MediaFormats.UpTo(1080));
    }

    [Fact]
    public void DownloadArgumentsForMergedVideo()
    {
        var args = YtDlpMediaTool.BuildDownloadArguments(YouTube, "out.%(ext)s", MediaFormats.UpTo(1080), 500_000, ["--cookies-from-browser", "firefox"]);

        Assert.Equal(MediaFormats.UpTo(1080), args[args.IndexOf("-f") + 1]);
        Assert.Equal("mp4", args[args.IndexOf("--merge-output-format") + 1]);
        Assert.Equal("500000", args[args.IndexOf("--limit-rate") + 1]);
        Assert.True(args.IndexOf("--cookies-from-browser") < args.IndexOf("--"));
        Assert.Equal(YouTube.AbsoluteUri, args[^1]);
        Assert.Contains("--ignore-config", args);
        Assert.Contains("--no-playlist", args);
    }

    [Fact]
    public void DownloadArgumentsForMp3()
    {
        var args = YtDlpMediaTool.BuildDownloadArguments(YouTube, "out.%(ext)s", MediaFormats.AudioMp3, null, []);

        Assert.Equal("ba/b", args[args.IndexOf("-f") + 1]);
        Assert.Equal("mp3", args[args.IndexOf("--audio-format") + 1]);
        Assert.Contains("--extract-audio", args);
        Assert.DoesNotContain("--merge-output-format", args);
        Assert.DoesNotContain("--limit-rate", args);
    }

    [Fact]
    public void SignInOptionsAreOptInAndValidated()
    {
        Assert.Empty(YtDlpMediaTool.CookieArguments(new AppSettings()));
        Assert.Equal(["--cookies-from-browser", "edge"], YtDlpMediaTool.CookieArguments(new AppSettings { MediaCookieBrowser = "Edge" }));
        Assert.Empty(YtDlpMediaTool.CookieArguments(new AppSettings { MediaCookieBrowser = "--exec calc" }));
        Assert.Equal(["--cookies", @"C:\c.txt"], YtDlpMediaTool.CookieArguments(new AppSettings { MediaCookieBrowser = "firefox", MediaCookieFile = @"C:\c.txt" }));
    }

    [Fact]
    public void BotCheckErrorsExplainTheSignInOption()
    {
        var message = YtDlpMediaTool.ExplainError("ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies-from-browser", "x");

        Assert.Contains("Settings > Media", message, StringComparison.Ordinal);
        Assert.StartsWith("x: ", YtDlpMediaTool.ExplainError("ERROR: something else", "x"), StringComparison.Ordinal);
    }
}
