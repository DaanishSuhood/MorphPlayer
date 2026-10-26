using System.IO;
using System.Text.Json;

namespace MorphPlayer;

/// <summary>What MorphPlayer remembers between runs, kept in %LOCALAPPDATA%\MorphPlayer\state.json.</summary>
sealed class AppState
{
    const int MaxResumePoints = 300;
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MorphPlayer", "state.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Where each local file was left, keyed by full path.</summary>
    public Dictionary<string, ResumePoint> Resume { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>LibVLC's id for the chosen output device; null follows the Windows default.</summary>
    public string? AudioDevice { get; set; }

    /// <summary>LibVLC's stereo mode (0 = original channels). See MainWindow.StereoModes.</summary>
    public int StereoMode { get; set; }

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) is { } state)
            {
                // The deserializer builds a case-sensitive dictionary; paths on Windows are not.
                state.Resume = new(state.Resume ?? new(), StringComparer.OrdinalIgnoreCase);
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
