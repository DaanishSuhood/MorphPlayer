using System.IO;
using System.Text.Json;

namespace MorphPlayer;

/// <summary>
/// What MorphPlayer remembers between runs, kept in %LOCALAPPDATA%\MorphPlayer\state.json: resume positions, audio
/// choices, the theme and accent colour, and when it last looked for updates.
/// </summary>
sealed class AppState
{
    const int MaxResumePoints = 300;
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MorphPlayer", "state.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Where each local file or website video was left, keyed by full path or page address.</summary>
    public Dictionary<string, ResumePoint> Resume { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>LibVLC's id for the chosen output device; null follows the Windows default.</summary>
    public string? AudioDevice { get; set; }

    /// <summary>LibVLC's stereo mode (0 = original channels). See MainWindow.StereoModes.</summary>
    public int StereoMode { get; set; }

    /// <summary>The highest resolution asked of yt-dlp for website links, such as 1080. See MainWindow.WebQualities.</summary>
    public int WebMaxHeight { get; set; } = 1080;

    /// <summary>The video codec preferred for website links: "vp9" (less data) or "h264" (faster starts and seeking).</summary>
    public string WebCodec { get; set; } = "vp9";

    /// <summary>A theme's id from Themes.All, or "system" to match Windows' dark or light app mode.</summary>
    public string Theme { get; set; } = Themes.DefaultTheme;

    /// <summary>The accent colour as "#RRGGBB", or "windows" to use the accent colour chosen in Windows Settings.</summary>
    public string Accent { get; set; } = Themes.DefaultAccent;

    /// <summary>Whether to ask GitHub for a newer MorphPlayer, and update yt-dlp, once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public DateTime? UpdateCheckedUtc { get; set; }

    /// <summary>The newest release GitHub reported at the last check, such as "1.1.0".</summary>
    public string? LatestVersion { get; set; }

    /// <summary>A release whose banner was closed. It isn't shown again; the Updates menu still offers it.</summary>
    public string? DismissedVersion { get; set; }

    public DateTime? YtDlpUpdatedUtc { get; set; }

    /// <summary>Set when someone says no to fetching yt-dlp, so opening a web link doesn't ask every time.</summary>
    public bool YtDlpDeclined { get; set; }

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) is { } state)
            {
                // The deserializer builds a case-sensitive dictionary; paths on Windows are not.
                state.Resume = new(state.Resume ?? new(), StringComparer.OrdinalIgnoreCase);
                // 0 was "Best Available", before the list became individual resolutions topping out at 2160p.
                if (state.WebMaxHeight <= 0) state.WebMaxHeight = 2160;
                return state;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged file just means starting fresh.
        }
        return new AppState();
    }

    public void Save()
    {
        foreach (var stale in Resume.OrderBy(p => p.Value.Saved).Take(Resume.Count - MaxResumePoints).Select(p => p.Key).ToList())
            Resume.Remove(stale);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write then rename, so a crash mid-write can't leave a half-written file behind.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to save is no reason to interrupt playback.
        }
    }
}

sealed record ResumePoint(long TimeMs, long LengthMs, DateTime Saved);
