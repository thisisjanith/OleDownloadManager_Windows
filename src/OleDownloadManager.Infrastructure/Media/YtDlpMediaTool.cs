using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Engine;

namespace OleDownloadManager.Infrastructure.Media;

/// <summary>Quality choices and their yt-dlp format selectors.</summary>
public static class MediaFormats
{
    public const string Best = "bv*+ba/b";
    public const string BestSingleFile = "b";
    public const string AudioOriginal = "ba/b";

    /// <summary>Selectors starting with this prefix also convert the audio to MP3 (needs ffmpeg).</summary>
    public const string Mp3Prefix = "mp3:";
    public const string AudioMp3 = Mp3Prefix + "ba/b";

    /// <summary>Best video no taller than <paramref name="height"/> plus best audio, or the best single file of that height.</summary>
    public static string UpTo(int height) => $"bv*[height<={height}]+ba/b[height<={height}]";

    /// <summary>Single-file (already merged) formats only, for systems without ffmpeg.</summary>
    public static string SingleFileUpTo(int height) => $"b[height<={height}]";

    public static string HeightLabel(int height) => height switch
    {
        >= 4320 => $"{height}p (8K)",
        >= 2160 => $"{height}p (4K)",
        >= 1440 => $"{height}p (2K)",
        >= 1080 => $"{height}p (Full HD)",
        >= 720 => $"{height}p (HD)",
        _ => $"{height}p",
    };

    /// <summary>Generic choices used before a page has been read and for playlists.</summary>
    public static IReadOnlyList<MediaQuality> Standard { get; } =
    [
        new("Best available", Best, null, false),
        .. new[] { 2160, 1440, 1080, 720, 480, 360 }.Select(h => new MediaQuality($"Up to {HeightLabel(h)}", UpTo(h), null, false)),
        new("Audio only (original format)", AudioOriginal, null, true),
        new("Audio only (MP3)", AudioMp3, null, true),
    ];

    /// <summary>Settings-page labels for the default quality.</summary>
    public static IReadOnlyList<(string Label, string Selector, bool AudioOnly)> All { get; } =
        Standard.Select(q => (q.Label, q.Selector, q.AudioOnly)).ToList();

    public static bool NeedsFfmpeg(string selector) =>
        selector.Contains('+', StringComparison.Ordinal) || selector.StartsWith(Mp3Prefix, StringComparison.Ordinal);
}

/// <summary>
/// Optional integration with a user-installed yt-dlp executable. The app never ships or embeds
/// site-specific extraction code; it only launches the tool the user installed, with user config
/// files ignored so behaviour is predictable. DRM-protected media is not supported.
/// </summary>
public sealed class YtDlpMediaTool : IMediaTool
{
    private const string ProgressPrefix = "OLEDM|";
    private const string FilePrefix = "OLEDMFILE|";
    private const int MaxPlaylistEntries = 5000;

    /// <summary>Browsers yt-dlp can read a sign-in from (values for --cookies-from-browser).</summary>
    public static IReadOnlyList<string> CookieBrowsers { get; } = ["firefox", "chrome", "edge", "brave", "opera", "vivaldi", "chromium"];

    private readonly ISettingsStore _settings;
    private readonly ILogger<YtDlpMediaTool> _logger;

    public YtDlpMediaTool(ISettingsStore settings, ILogger<YtDlpMediaTool> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string? LocateExecutable()
    {
        var configured = _settings.Current.MediaToolPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        return FindOnPath("yt-dlp.exe");
    }

    /// <summary>ffmpeg is needed to merge separate video/audio streams (most HD video) and to convert to MP3.</summary>
    public static bool IsFfmpegAvailable() => FindOnPath("ffmpeg.exe") is not null;

    private static string? FindOnPath(string fileName)
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
        foreach (var directory in candidates)
        {
            try
            {
                var path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return null;
    }

    public async Task<MediaToolStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var path = LocateExecutable();
        var ffmpeg = IsFfmpegAvailable();
        if (path is null)
        {
            return new MediaToolStatus(false, null, null, "yt-dlp was not found. Install it (for example: winget install yt-dlp.yt-dlp) or set its path in Settings.", ffmpeg);
        }

        try
        {
            var result = await RunAsync(path, ["--ignore-config", "--version"], null, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0
                ? new MediaToolStatus(true, path, result.StdOut.Trim(), null, ffmpeg)
                : new MediaToolStatus(false, path, null, "yt-dlp did not start correctly: " + LastError(result), ffmpeg);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            return new MediaToolStatus(false, path, null, "yt-dlp could not be started: " + ex.Message, ffmpeg);
        }
    }

    /// <summary>
    /// Updates yt-dlp: through winget when it was installed with winget (self-update is disabled there),
    /// otherwise with yt-dlp's own updater.
    /// </summary>
    public async Task<MediaToolStatus> UpdateAsync(CancellationToken cancellationToken = default)
    {
        var path = RequireExecutable();
        var winget = FindOnPath("winget.exe");
        var viaWinget = winget is not null && path.Contains(@"\Microsoft\WinGet\", StringComparison.OrdinalIgnoreCase);
        var executable = viaWinget ? winget! : path;
        IReadOnlyList<string> args = viaWinget
            ? ["upgrade", "--id", "yt-dlp.yt-dlp", "--exact", "--silent", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"]
            : ["--ignore-config", "-U"];
        _logger.LogInformation("Updating yt-dlp via {Method}", viaWinget ? "winget" : "yt-dlp -U");
        var result = await RunAsync(executable, args, null, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var output = result.StdOut + string.Join("\n", result.StdErr);
        var upToDate = output.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("No newer package", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("is up to date", StringComparison.OrdinalIgnoreCase);
        var message = upToDate ? "yt-dlp is already up to date."
            : result.ExitCode == 0 ? "yt-dlp was updated."
            : "The update did not complete: " + LastError(result);
        return status with { Message = message };
    }

    /// <summary>Sign-in options the user chose in Settings (none by default).</summary>
    internal static IReadOnlyList<string> CookieArguments(Core.Models.AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.MediaCookieFile))
        {
            return ["--cookies", settings.MediaCookieFile];
        }

        if (settings.MediaCookieBrowser is { } browser && CookieBrowsers.Contains(browser, StringComparer.OrdinalIgnoreCase))
        {
            return ["--cookies-from-browser", browser.ToLowerInvariant()];
        }

        return [];
    }

    public async Task<MediaInspection> InspectAsync(Uri url, CancellationToken cancellationToken)
    {
        var path = RequireExecutable();
        var args = new List<string> { "--ignore-config", "--flat-playlist", "--dump-single-json", "--no-warnings" };
        args.AddRange(CookieArguments(_settings.Current));
        args.Add("--");
        args.Add(url.AbsoluteUri);
        var result = await RunAsync(path, args, null, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new DownloadException(DownloadErrorKind.MediaToolFailed, ExplainError(LastError(result), "Could not read the media page"), false);
        }

        return ParseInspection(result.StdOut, url, IsFfmpegAvailable());
    }

    internal static MediaInspection ParseInspection(string json, Uri requestUrl, bool ffmpegAvailable = true)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        var title = GetString(root, "title") ?? requestUrl.Host;
        var entries = new List<MediaEntry>();
        if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var entry in list.EnumerateArray())
            {
                if (entries.Count >= MaxPlaylistEntries)
                {
                    break;
                }

                index++;
                var entryUrl = GetString(entry, "webpage_url") ?? GetString(entry, "url");
                if (entryUrl is null || !Uri.TryCreate(entryUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                {
                    continue;
                }

                entries.Add(new MediaEntry(GetString(entry, "id") ?? index.ToString(CultureInfo.InvariantCulture),
                    GetString(entry, "title") ?? $"Item {index}", uri, GetDuration(entry), index));
            }

            return new MediaInspection(title, true, entries, StandardQualities(ffmpegAvailable));
        }

        var single = GetString(root, "webpage_url") is { } page && Uri.TryCreate(page, UriKind.Absolute, out var pageUri) ? pageUri : requestUrl;
        entries.Add(new MediaEntry(GetString(root, "id") ?? "1", title, single, GetDuration(root), 1));
        var qualities = ParseQualities(root, ffmpegAvailable);
        return new MediaInspection(title, false, entries, qualities.Count > 0 ? qualities : StandardQualities(ffmpegAvailable));
    }

    private static IReadOnlyList<MediaQuality> StandardQualities(bool ffmpegAvailable) =>
        ffmpegAvailable
            ? MediaFormats.Standard
            : [new("Best available (single file)", MediaFormats.BestSingleFile, null, false), new("Audio only (original format)", MediaFormats.AudioOriginal, null, true)];

    private sealed record FormatInfo(int? Height, bool HasVideo, bool HasAudio, long? Size, bool SizeKnown, double Bitrate,
        bool IsH264, bool IsAac, bool IsStreamingPlaylist, int? Channels, bool IsDrc);

    /// <summary>
    /// Builds the quality list from the formats a single video really offers: one entry per resolution
    /// (best first) with an approximate size, then audio-only options. Size estimates follow the same
    /// preferences the download uses (H.264 and stereo AAC, direct HTTPS over HLS).
    /// </summary>
    internal static IReadOnlyList<MediaQuality> ParseQualities(JsonElement root, bool ffmpegAvailable)
    {
        if (!root.TryGetProperty("formats", out var formatsElement) || formatsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var duration = GetDuration(root)?.TotalSeconds;
        var formats = new List<FormatInfo>();
        foreach (var f in formatsElement.EnumerateArray())
        {
            var vcodec = GetString(f, "vcodec");
            var acodec = GetString(f, "acodec");
            var height = GetInt(f, "height");
            var hasVideo = vcodec is not null ? vcodec != "none" : height is > 0;
            var hasAudio = acodec is not null ? acodec != "none" : !hasVideo || vcodec is null;
            if (!hasVideo && !hasAudio)
            {
                continue;
            }

            if (GetString(f, "format_note") is { } note && note.Contains("storyboard", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bitrate = GetDouble(f, "tbr") ?? 0;
            var known = GetLong(f, "filesize") ?? GetLong(f, "filesize_approx");
            var size = known ?? (bitrate > 0 && duration is > 0 ? (long)(bitrate * 1000 / 8 * duration.Value) : null);
            formats.Add(new FormatInfo(
                hasVideo ? height : null, hasVideo, hasAudio, size, known is not null, bitrate,
                IsH264: vcodec?.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) == true,
                IsAac: acodec?.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) == true,
                IsStreamingPlaylist: GetString(f, "protocol")?.StartsWith("m3u8", StringComparison.OrdinalIgnoreCase) == true,
                Channels: GetInt(f, "audio_channels"),
                IsDrc: GetString(f, "format_id")?.Contains("drc", StringComparison.OrdinalIgnoreCase) == true));
        }

        var audioOnly = formats.Where(f => f.HasAudio && !f.HasVideo).ToList();
        // The audio track merged with video: stereo AAC when available, as the download prefers.
        var mergeAudio = audioOnly
            .OrderByDescending(f => f.IsAac)
            .ThenByDescending(f => f.Channels is null or <= 2)
            .ThenBy(f => f.IsDrc)
            .ThenByDescending(f => f.SizeKnown)
            .ThenByDescending(f => f.Bitrate)
            .FirstOrDefault();
        var bestAudio = audioOnly.Where(f => !f.IsDrc).OrderByDescending(f => f.Bitrate).FirstOrDefault() ?? mergeAudio;

        var result = new List<MediaQuality>();
        var heights = formats.Where(f => f.HasVideo && f.Height is > 0).Select(f => f.Height!.Value).Distinct().OrderDescending();
        foreach (var height in heights)
        {
            var atHeight = formats.Where(f => f.HasVideo && f.Height == height).ToList();
            var combined = atHeight.Where(f => f.HasAudio).OrderByDescending(f => f.Bitrate).FirstOrDefault();
            var videoOnly = atHeight.Where(f => !f.HasAudio)
                .OrderByDescending(f => f.IsH264)
                .ThenBy(f => f.IsStreamingPlaylist)
                .ThenByDescending(f => f.SizeKnown)
                .ThenByDescending(f => f.Bitrate)
                .FirstOrDefault();
            if (ffmpegAvailable && videoOnly is not null)
            {
                var size = videoOnly.Size is { } v ? v + (mergeAudio?.Size ?? 0) : (long?)null;
                result.Add(new MediaQuality(MediaFormats.HeightLabel(height), MediaFormats.UpTo(height), size, false));
            }
            else if (combined is not null)
            {
                result.Add(new MediaQuality(MediaFormats.HeightLabel(height),
                    ffmpegAvailable ? MediaFormats.UpTo(height) : MediaFormats.SingleFileUpTo(height), combined.Size, false));
            }
        }

        if (formats.Any(f => f.HasAudio))
        {
            result.Add(new MediaQuality("Audio only (original format)", MediaFormats.AudioOriginal, bestAudio?.Size, true));
            if (ffmpegAvailable)
            {
                // MP3 at the highest VBR quality averages roughly 245 kbit/s.
                long? mp3 = duration is > 0 ? (long)(245_000 / 8 * duration.Value) : null;
                result.Add(new MediaQuality("Audio only (MP3)", MediaFormats.AudioMp3, mp3, true));
            }
        }

        return result;
    }

    /// <summary>yt-dlp arguments for one download. Kept separate so the exact command line is testable.</summary>
    internal static List<string> BuildDownloadArguments(Uri url, string outputTemplate, string format, long? rateLimitBytesPerSecond, IReadOnlyList<string> cookieArguments)
    {
        var args = new List<string>
        {
            "--ignore-config", "--no-playlist", "--newline", "--progress", "--no-warnings",
            "--windows-filenames", "--no-mtime", "--continue",
            "-o", outputTemplate,
            "--progress-template", $"download:{ProgressPrefix}%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s",
            "--print", $"after_move:{FilePrefix}%(filepath)s",
            "--no-simulate",
        };

        if (format.StartsWith(MediaFormats.Mp3Prefix, StringComparison.Ordinal))
        {
            args.AddRange(["-f", format[MediaFormats.Mp3Prefix.Length..], "--extract-audio", "--audio-format", "mp3", "--audio-quality", "0"]);
        }
        else
        {
            args.AddRange(["-f", format]);
            if (format.Contains('+', StringComparison.Ordinal))
            {
                // Highest allowed resolution first, then prefer H.264/AAC so the MP4 plays in every Windows player
                // (YouTube offers only VP9/AV1 above 1080p).
                args.AddRange(["--format-sort", "res,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4"]);
            }
        }

        if (rateLimitBytesPerSecond is > 0)
        {
            args.Add("--limit-rate");
            args.Add(rateLimitBytesPerSecond.Value.ToString(CultureInfo.InvariantCulture));
        }

        args.AddRange(cookieArguments);
        args.Add("--");
        args.Add(url.AbsoluteUri);
        return args;
    }

    public async Task<string> DownloadAsync(Uri url, string outputDirectory, string fileStem, string format,
        long? rateLimitBytesPerSecond, IProgress<MediaProgress> progress, CancellationToken cancellationToken)
    {
        var path = RequireExecutable();
        if (MediaFormats.NeedsFfmpeg(format) && !IsFfmpegAvailable())
        {
            throw new DownloadException(DownloadErrorKind.MediaToolFailed,
                "This quality needs ffmpeg to combine video and audio. Install it (winget install Gyan.FFmpeg) or choose a single-file quality.", false);
        }

        var template = Path.Combine(outputDirectory, fileStem.Replace("%", "%%", StringComparison.Ordinal) + ".%(ext)s");
        var args = BuildDownloadArguments(url, template, format, rateLimitBytesPerSecond, CookieArguments(_settings.Current));

        string? producedFile = null;
        var tracker = new ProgressTracker(progress);
        var result = await RunAsync(path, args, line =>
        {
            if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            {
                tracker.Report(line[ProgressPrefix.Length..]);
            }
            else if (line.StartsWith(FilePrefix, StringComparison.Ordinal))
            {
                producedFile = line[FilePrefix.Length..].Trim();
            }
        }, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new DownloadException(DownloadErrorKind.MediaToolFailed, ExplainError(LastError(result), "yt-dlp failed"), false);
        }

        if (producedFile is null || !File.Exists(producedFile))
        {
            throw new DownloadException(DownloadErrorKind.MediaToolFailed, "yt-dlp finished but did not report the downloaded file.", false);
        }

        var full = Path.GetFullPath(producedFile);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(outputDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new DownloadException(DownloadErrorKind.MediaToolFailed, "yt-dlp wrote the file outside the expected folder.", false);
        }

        return full;
    }

    /// <summary>Turns yt-dlp's last error line into a short message, with guidance for sign-in and cookie problems.</summary>
    internal static string ExplainError(string error, string prefix)
    {
        if (error.Contains("not a bot", StringComparison.OrdinalIgnoreCase) || error.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("LOGIN_REQUIRED", StringComparison.Ordinal))
        {
            return "The site asked to confirm you are not a bot (it is limiting downloads from your network). " +
                   "In Settings > Media, choose a browser you are signed in to (Firefox works most reliably) or a cookies.txt file, then try again.";
        }

        if (error.Contains("cookie", StringComparison.OrdinalIgnoreCase) &&
            (error.Contains("Could not copy", StringComparison.OrdinalIgnoreCase) || error.Contains("decrypt", StringComparison.OrdinalIgnoreCase)))
        {
            return "yt-dlp could not read the browser's sign-in. Close that browser and try again, use Firefox, or export a cookies.txt file (Settings > Media).";
        }

        if (error.Contains("HTTP Error 403", StringComparison.Ordinal) || error.Contains("nsig", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Signature extraction failed", StringComparison.OrdinalIgnoreCase) || error.Contains("PO Token", StringComparison.OrdinalIgnoreCase))
        {
            return "The site refused the video stream. Sites like YouTube change often and older yt-dlp versions stop working - " +
                   "update it with Settings > Media > Update yt-dlp, then retry. (" + error.Replace("ERROR: ", "", StringComparison.Ordinal) + ")";
        }

        if (error.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase))
        {
            return "That quality is not available for this video. Choose another quality.";
        }

        if (error.Contains("Private video", StringComparison.OrdinalIgnoreCase) || error.Contains("members-only", StringComparison.OrdinalIgnoreCase))
        {
            return "This video is private or restricted.";
        }

        if (error.Contains("DRM", StringComparison.Ordinal))
        {
            return "This media is DRM-protected and cannot be downloaded.";
        }

        return $"{prefix}: {error}";
    }

    private string RequireExecutable() =>
        LocateExecutable() ?? throw new DownloadException(DownloadErrorKind.MediaToolFailed,
            "yt-dlp was not found. Install it (winget install yt-dlp.yt-dlp) or set its path in Settings.", false);

    private sealed record ProcessResult(int ExitCode, string StdOut, IReadOnlyList<string> StdErr);

    private async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, Action<string>? onLine,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = start };
        var stdout = new StringBuilder();
        var stderr = new List<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (onLine is null)
            {
                stdout.AppendLine(e.Data);
            }
            else
            {
                onLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (onLine is not null && e.Data.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            {
                onLine(e.Data);
                return;
            }

            lock (stderr)
            {
                stderr.Add(e.Data);
                if (stderr.Count > 200)
                {
                    stderr.RemoveAt(0);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            linked.CancelAfter(timeout);
        }

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("yt-dlp did not respond in time.");
        }

        // Ensure asynchronous readers have drained.
        process.WaitForExit();
        _logger.LogDebug("yt-dlp exited with {Code}", process.ExitCode);
        lock (stderr)
        {
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToList());
        }
    }

    private static string LastError(ProcessResult result)
    {
        var error = result.StdErr.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal))
            ?? result.StdErr.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))
            ?? $"exit code {result.ExitCode}";
        return error.Length > 400 ? error[..400] : error;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        GetDouble(element, name) is { } d && d is > 0 and < int.MaxValue ? (int)d : null;

    private static long? GetLong(JsonElement element, string name) =>
        GetDouble(element, name) is { } d && d is > 0 and < long.MaxValue ? (long)d : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static TimeSpan? GetDuration(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var seconds) && seconds is > 0 and < 1e7
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>Turns yt-dlp's per-stream progress into one monotonically increasing byte count.</summary>
    internal sealed class ProgressTracker(IProgress<MediaProgress> progress)
    {
        private long _completedStreams;
        private long _lastDownloaded;
        private long _lastTotal;

        public void Report(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length < 4)
            {
                return;
            }

            var downloaded = ParseLong(parts[0]) ?? 0;
            var total = ParseLong(parts[1]) ?? ParseLong(parts[2]);
            var speed = ParseDouble(parts[3]) ?? 0;

            if (downloaded + (64 * 1024) < _lastDownloaded)
            {
                // A new stream (e.g. audio after video) started.
                _completedStreams += Math.Max(_lastTotal, _lastDownloaded);
            }

            _lastDownloaded = downloaded;
            _lastTotal = total ?? _lastTotal;
            progress.Report(new MediaProgress(_completedStreams + downloaded, total is { } t ? _completedStreams + t : null, speed, null));
        }

        private static long? ParseLong(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) && d >= 0 ? (long)d : null;

        private static double? ParseDouble(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) && d >= 0 ? d : null;
    }
}
