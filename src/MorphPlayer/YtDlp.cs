using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MorphPlayer;

/// <summary>
/// Plays YouTube and other website links by asking yt-dlp for the stream behind the page, which LibVLC then plays.
/// Like SnagDL, MorphPlayer fetches and updates its own yt-dlp.exe, plus Deno, the JavaScript runtime yt-dlp needs
/// to solve YouTube's challenges. Both go in %LOCALAPPDATA%\MorphPlayer\tools, which is always writable, even when
/// MorphPlayer itself is installed for all users in Program Files.
/// </summary>
sealed class YtDlp
{
    static readonly string ToolsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MorphPlayer", "tools");
    static string ExePath => Path.Combine(ToolsDir, "yt-dlp.exe");
    static string DenoPath => Path.Combine(ToolsDir, "deno.exe");

    // Nightly, as in SnagDL: YouTube extractor fixes reach it well before stable.
    const string YtDlpRelease = "https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/";
    const string DenoZip = "deno-x86_64-pc-windows-msvc.zip"; // also runs on Windows on ARM, as MorphPlayer does
    const string DenoRelease = "https://github.com/denoland/deno/releases/latest/download/";

    // YouTube serves its best video and audio as separate streams, which LibVLC plays together (the audio as a slave
    // of the video). Asked for explicitly, because without ffmpeg yt-dlp assumes they can't be merged and falls back
    // to the best single stream, 360p on YouTube.
    // AV1 is left out unless a page has nothing else: ranking it last in FormatSort isn't enough, because resolution
    // ranks first, so a size offered only in AV1 would still win. The "?" keeps formats whose codec isn't known.
    // Plain files (http/https) come first. YouTube also offers some videos as HLS playlists (format 616, "Premium"
    // VP9), which yt-dlp ranks above the WebM file of the same size for its higher bitrate. LibVLC 3 plays that HLS
    // video with the audio as a slave, but given a start time (resuming, or a "&t=" link) it sits at 0:00 with no
    // picture, and the stream also bypasses the relay and AudioLeads. Live streams, which only come as HLS, fall
    // through to the later choices.
    const string Format = "bv*[protocol^=http][vcodec!^=?av01]+ba[protocol^=http]/bv*[vcodec!^=?av01]+ba/bv*+ba/b";

    /// <summary>
    /// yt-dlp's format sort for a resolution limit and codec: the largest video within the limit, then the highest
    /// frame rate, then the codec, falling back to the other where a video lacks it. YouTube has H.264 only up to
    /// 1080p, so above that it's VP9 whichever is preferred.
    /// VP9 looks as good as H.264 at about three quarters of the bitrate, and GPUs since about 2016 decode it, but in
    /// LibVLC 3 it starts and seeks about half as fast (see WebStream.AudioLeads). AV1 is left out either way (see
    /// Format): LibVLC 3 decodes it on the CPU, which 4K60 can stutter under. yt-dlp measures resolution by the shorter side,
    /// so 4K ultrawide (3840×1608) counts as within 2160p, and a vertical 1080×1920 video as 1080p.
    /// Audio is AAC, in an M4A file, rather than Opus in WebM: see WebStream.AudioLeads for why.
    /// </summary>
    static string FormatSort(int maxHeight, string codec) =>
        $"res:{maxHeight},fps,vcodec:{(codec == "h264" ? "h264" : "vp9")},acodec:aac";

    // An update replaces yt-dlp.exe, so it never runs alongside anything else.
    readonly SemaphoreSlim _lock = new(1, 1);

    public static bool IsInstalled => File.Exists(ExePath) && File.Exists(DenoPath);

    /// <summary>Downloads yt-dlp.exe and deno.exe, each checked against its published SHA-256.</summary>
    public async Task InstallAsync(IProgress<string> status, CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel);
        try
        {
            Directory.CreateDirectory(ToolsDir);
            await Web.DownloadVerifiedAsync(YtDlpRelease + "yt-dlp.exe", YtDlpRelease + "SHA2-256SUMS", "yt-dlp.exe", ExePath,
                new Progress<int>(p => status.Report($"Downloading yt-dlp… {p}%")), cancel);

            var zip = Path.Combine(ToolsDir, DenoZip);
            try
            {
                await Web.DownloadVerifiedAsync(DenoRelease + DenoZip, DenoRelease + DenoZip + ".sha256sum", DenoZip, zip,
                    new Progress<int>(p => status.Report($"Downloading Deno… {p}%")), cancel);
                status.Report("Unpacking Deno…");
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(zip);
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
                                ?? throw new IOException($"deno.exe is not in {DenoZip}.");
                    entry.ExtractToFile(DenoPath, overwrite: true);
                }, cancel);
            }
            finally
            {
                File.Delete(zip);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Updates yt-dlp to the newest nightly and returns its version afterwards.</summary>
    public async Task<string> UpdateAsync(CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel);
        try
        {
            var result = await RunAsync(["--update-to", "nightly"], cancel);
            if (result.ExitCode != 0) throw new YtDlpException(ErrorLine(result.Error) ?? "yt-dlp could not update itself.");
            return (await RunAsync(["--version"], cancel)).Output.Trim();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Asks yt-dlp what plays at a web page's address, at a resolution of at most maxHeight (such as 1080), in codec
    /// ("vp9" or "h264") where it can.
    /// </summary>
    public async Task<WebStream> ResolveAsync(string url, int maxHeight, string codec, CancellationToken cancel)
    {
        await _lock.WaitAsync(cancel);
        (int ExitCode, string Output, string Error) result;
        try
        {
            result = await RunAsync(["-J", "--no-playlist", "--no-warnings", "--js-runtimes", "deno:" + DenoPath,
                                     "-f", Format, "-S", FormatSort(maxHeight, codec), "--", url], cancel);
        }
        finally
        {
            _lock.Release();
        }
        if (result.ExitCode != 0)
        {
            var error = ErrorLine(result.Error) ?? "yt-dlp could not read this page.";
            throw new YtDlpException(error, unsupported: result.Error.Contains("Unsupported URL", StringComparison.Ordinal),
                                     rateLimited: IsRateLimited(result.Error));
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            return WebStream.FromJson(doc.RootElement, maxHeight, codec);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new YtDlpException("yt-dlp's answer couldn't be read.");
        }
    }

    static async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] args, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Otherwise Python writes the console's code page, and titles in other scripts come back as question marks.
        start.Environment["PYTHONUTF8"] = "1";
        start.ArgumentList.Add("--encoding");
        start.ArgumentList.Add("utf-8");
        foreach (var arg in args) start.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new YtDlpException("yt-dlp did not start.");
        }
        catch (Win32Exception e)
        {
            // Usually antivirus: yt-dlp.exe is a common false positive.
            throw new YtDlpException($"yt-dlp did not start: {e.Message}");
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            return (process.ExitCode, await output, await error);
        }
    }

    /// <summary>
    /// Whether the site turned yt-dlp away for asking too often: an HTTP 429, YouTube's "Sign in to confirm you're not
    /// a bot" check, or its "session has been rate-limited". Matched loosely, as YouTube words them with either a
    /// straight or a curly apostrophe and yt-dlp rewords them from time to time.
    /// </summary>
    static bool IsRateLimited(string stderr) =>
        stderr.Contains("HTTP Error 429", StringComparison.Ordinal)
        || stderr.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("not a bot", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("rate-limited", StringComparison.OrdinalIgnoreCase);

    /// <summary>yt-dlp's last "ERROR:" line, without the prefix and the "[youtube] id:" before the message.</summary>
    static string? ErrorLine(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        if (line == null) return null;
        line = line["ERROR:".Length..].Trim();
        if (line.StartsWith('[') && line.IndexOf("]", StringComparison.Ordinal) is var close and > 0)
        {
            line = line[(close + 1)..].Trim();
            if (line.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0 && !line[..colon].Contains(' ')) line = line[(colon + 2)..];
        }
        return line;
    }
}

/// <summary>One set of captions for a web video; Language is the site's code for it, such as "en" or "pt-BR".</summary>
public sealed record WebCaption(string Label, string Url, string Language, bool Auto);

sealed class YtDlpException(string message, bool unsupported = false, bool rateLimited = false) : Exception(message)
{
    /// <summary>yt-dlp doesn't know the site, so the address may well be a plain stream LibVLC can open itself.</summary>
    public bool Unsupported { get; } = unsupported;

    /// <summary>The site is limiting requests from this network for now; only waiting helps.</summary>
    public bool RateLimited { get; } = rateLimited;
}

/// <summary>What yt-dlp found behind a web page: the stream to play, and the page's title and thumbnail.</summary>
public sealed class WebStream
{
    // Sites' stream addresses expire (YouTube's after about six hours), so an older answer is asked for again.
    static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);

    public required string Url { get; init; }
    /// <summary>A separate audio stream to play alongside, when the site serves video and audio apart.</summary>
    public string? AudioUrl { get; init; }
    /// <summary>LibVLC options for the headers some sites check, such as the Referer.</summary>
    public required IReadOnlyList<string> Options { get; init; }
    /// <summary>The headers yt-dlp would send for these streams, for StreamRelay.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    /// <summary>
    /// Whether each stream is a plain file over http(s), which goes through StreamRelay. Stream playlists (HLS, DASH),
    /// as live streams use, already come in small pieces and go to LibVLC directly.
    /// </summary>
    public bool RelayVideo { get; init; }
    public bool RelayAudio { get; init; }
    /// <summary>
    /// Whether to open the audio as the main stream, with the video added to it. LibVLC 3 moves a second stream to
    /// wherever the main one says it is after a seek, and its WebM reader reports the old position, so with WebM
    /// video in front every seek and start time left the audio behind and playback stalled. Its MP4 reader
    /// reports the new one, so when the video is WebM (YouTube's VP9) and the audio is M4A, the audio leads.
    /// </summary>
    public bool AudioLeads { get; init; }
    /// <summary>Where the link says to start, as in YouTube's "&amp;t=58s".</summary>
    public long StartMs { get; init; }
    /// <summary>The page's own address, without extras such as "&amp;t=58s": what its resume position is kept under.</summary>
    public string? PageUrl { get; init; }
    /// <summary>A live stream, which has no position worth coming back to.</summary>
    public bool IsLive { get; init; }
    public IReadOnlyList<WebCaption> Captions { get; init; } = [];
    public string? Title { get; init; }
    public string? Uploader { get; init; }
    public long DurationMs { get; init; }
    public string? ThumbnailUrl { get; init; }
    public bool HasVideo { get; init; }
    /// <summary>The video's resolution (its shorter side) and frame rate, when yt-dlp knows them, for the quality button.</summary>
    public int Height { get; init; }
    /// <summary>Every resolution the page offers in a codec MorphPlayer would pick (AV1-only ones left out).</summary>
    public IReadOnlySet<int> Resolutions { get; init; } = new HashSet<int>();
    public double Fps { get; init; }
    /// <summary>The resolution limit and codec it was looked up with, so a change of setting asks again.</summary>
    public int MaxHeight { get; init; }
    public string Codec { get; init; } = "vp9";
    public DateTime ResolvedUtc { get; } = DateTime.UtcNow;
    /// <summary>
    /// When the stream addresses start working. YouTube now hands out addresses that answer 403 Forbidden for the
    /// first few seconds (yt-dlp's "Sleeping 5.00 seconds as required by the site"), and LibVLC gives up on a 403, so
    /// playback waits until then. Long videos tend to have the longer waits.
    /// </summary>
    public DateTime AvailableUtc { get; init; }

    public bool IsFresh => DateTime.UtcNow - ResolvedUtc < FreshFor;

    public static WebStream FromJson(JsonElement info, int maxHeight, string codec)
    {
        string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        bool IsNone(JsonElement f, string codec) => Text(f, codec) == "none";
        bool IsPlainFile(JsonElement f) => Text(f, "protocol") is "https" or "http";

        // A merged format lists its parts in requested_formats; a single one has its url at the top.
        JsonElement main = info;
        JsonElement? audio = null;
        if (info.TryGetProperty("requested_formats", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0)
        {
            var formats = parts.EnumerateArray().ToList();
            var video = formats.FindIndex(f => !IsNone(f, "vcodec"));
            main = formats[Math.Max(0, video)];
            audio = formats.Where((f, i) => i != Math.Max(0, video) && IsNone(f, "vcodec") && Text(f, "url") != null)
                           .Cast<JsonElement?>().FirstOrDefault();
        }
        var url = Text(main, "url") ?? throw new InvalidOperationException("No stream URL.");
        // yt-dlp's available_at, in Unix seconds, for each stream played.
        double AvailableAt(JsonElement f) =>
            f.TryGetProperty("available_at", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var availableAt = Math.Max(Math.Max(AvailableAt(info), AvailableAt(main)), audio is { } x ? AvailableAt(x) : 0);

        var options = new List<string>();
        var headerValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (main.TryGetProperty("http_headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headers.EnumerateObject())
                if (header.Value.ValueKind == JsonValueKind.String) headerValues[header.Name] = header.Value.GetString()!;
            if (Text(headers, "User-Agent") is { } agent) options.Add(":http-user-agent=" + agent);
            if (Text(headers, "Referer") is { } referer) options.Add(":http-referrer=" + referer);
        }

        return new WebStream
        {
            Url = url,
            AudioUrl = audio is { } a ? Text(a, "url") : null,
            Options = options,
            Headers = headerValues,
            RelayVideo = IsPlainFile(main),
            RelayAudio = audio is { } b && IsPlainFile(b),
            AudioLeads = audio is { } c && IsPlainFile(main) && IsPlainFile(c)
                         && Text(main, "ext") == "webm" && Text(c, "ext") is "m4a" or "mp4",
            AvailableUtc = availableAt > 0 ? DateTime.UnixEpoch.AddSeconds(availableAt) : DateTime.MinValue,
            StartMs = info.TryGetProperty("start_time", out var t) && t.ValueKind == JsonValueKind.Number ? (long)(t.GetDouble() * 1000) : 0,
            PageUrl = Text(info, "webpage_url"),
            IsLive = Text(info, "live_status") is "is_live" or "is_upcoming" or "post_live",
            Captions = FindCaptions(info),
            Title = Text(info, "title"),
            Uploader = Text(info, "uploader") ?? Text(info, "channel"),
            DurationMs = info.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (long)(d.GetDouble() * 1000) : 0,
            ThumbnailUrl = Thumbnail(info) ?? Text(info, "thumbnail"),
            HasVideo = !IsNone(main, "vcodec"),
            Height = Resolution(main),
            Resolutions = info.TryGetProperty("formats", out var all) && all.ValueKind == JsonValueKind.Array
                ? all.EnumerateArray()
                     .Where(f => !IsNone(f, "vcodec") && Text(f, "vcodec")?.StartsWith("av01", StringComparison.Ordinal) != true)
                     .Select(Resolution).Where(r => r > 0).ToHashSet()
                : new HashSet<int>(),
            Fps = main.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0,
            MaxHeight = maxHeight,
            Codec = codec,
        };
    }

    /// <summary>
    /// The page's captions, as WebVTT, which LibVLC reads: every set the uploader added, and the speech-recognition
    /// set in the video's own language. YouTube also offers that one machine-translated into some 150 languages;
    /// those are left out.
    /// </summary>
    static List<WebCaption> FindCaptions(JsonElement info)
    {
        var captions = new List<WebCaption>();
        if (info.TryGetProperty("subtitles", out var uploaded) && uploaded.ValueKind == JsonValueKind.Object)
        {
            foreach (var track in uploaded.EnumerateObject())
            {
                if (track.Name == "live_chat" || Vtt(track.Value) is not { } vtt) continue;
                captions.Add(new WebCaption(StringOf(vtt, "name") ?? LanguageName(track.Name), StringOf(vtt, "url")!, track.Name, Auto: false));
            }
        }
        if (info.TryGetProperty("automatic_captions", out var auto) && auto.ValueKind == JsonValueKind.Object)
        {
            // yt-dlp marks the untranslated set "-orig"; failing that, it's the one in the language the video is in.
            var tracks = auto.EnumerateObject().ToList();
            var spoken = StringOf(info, "language");
            var original = tracks.FindIndex(t => t.Name.EndsWith("-orig", StringComparison.Ordinal));
            if (original < 0 && spoken != null) original = tracks.FindIndex(t => t.Name == spoken);
            if (original >= 0 && Vtt(tracks[original].Value) is { } vtt)
            {
                var code = tracks[original].Name.Replace("-orig", "");
                captions.Add(new WebCaption($"{LanguageName(code)} (auto-generated)", StringOf(vtt, "url")!, code, Auto: true));
            }
        }
        return captions;
    }

    /// <summary>A format's shorter side, as yt-dlp measures resolution; 0 if it doesn't say.</summary>
    static int Resolution(JsonElement format)
    {
        static int Number(JsonElement f, string name) =>
            f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
        var (width, height) = (Number(format, "width"), Number(format, "height"));
        return width > 0 && height > 0 ? Math.Min(width, height) : height;
    }

    /// <summary>The WebVTT entry among a caption set's formats.</summary>
    static JsonElement? Vtt(JsonElement formats) =>
        formats.ValueKind != JsonValueKind.Array ? null
            : formats.EnumerateArray().Where(f => StringOf(f, "ext") == "vtt" && StringOf(f, "url") != null)
                     .Cast<JsonElement?>().FirstOrDefault();

    static string? StringOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>"en" as "English", in the Windows display language.</summary>
    static string LanguageName(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>
    /// The best JPEG or PNG thumbnail. yt-dlp's own pick is often WebP, which WPF only reads where the Windows WebP
    /// codec happens to be installed. The list runs from worst to best.
    /// </summary>
    static string? Thumbnail(JsonElement info)
    {
        if (!info.TryGetProperty("thumbnails", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        return list.EnumerateArray()
            .Select(t => t.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null)
            .LastOrDefault(u => Uri.TryCreate(u, UriKind.Absolute, out var uri)
                                && Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png");
    }
}
