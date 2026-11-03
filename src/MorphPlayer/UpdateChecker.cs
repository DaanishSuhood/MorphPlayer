using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace MorphPlayer;

/// <summary>
/// Asks GitHub for MorphPlayer's latest release, which the banner and the Updates menu then offer. Installing it is
/// Updater's job, and only happens when asked.
/// </summary>
static class UpdateChecker
{
    internal const string LatestReleaseApi = "https://api.github.com/repos/DaanishSuhood/MorphPlayer/releases/latest";
    const string ReleasePage = "https://github.com/DaanishSuhood/MorphPlayer/releases/latest";
    static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    /// <summary>This build's version, from &lt;Version&gt; in MorphPlayer.csproj, as major.minor.patch.</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateChecker).Assembly.GetName().Version);

    public static bool IsDue(AppState state) =>
        state.CheckForUpdates && (state.UpdateCheckedUtc is not { } last || DateTime.UtcNow - last >= CheckEvery || last > DateTime.UtcNow);

    /// <summary>Asks GitHub now and records the answer in state. False if GitHub couldn't be reached.</summary>
    public static async Task<bool> CheckAsync(AppState state)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var doc = JsonDocument.Parse(await Web.Client.GetStringAsync(LatestReleaseApi, limit.Token));
            if (!TryParse(doc.RootElement.GetProperty("tag_name").GetString(), out var latest)) return false;
            state.LatestVersion = latest.ToString();
            state.UpdateCheckedUtc = DateTime.UtcNow;
            state.Save();
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The newer release the last check found, if there is one.</summary>
    public static Version? Available(AppState state) =>
        TryParse(state.LatestVersion, out var latest) && latest > CurrentVersion ? latest : null;

    public static void OpenReleasePage() => Process.Start(new ProcessStartInfo(ReleasePage) { UseShellExecute = true });

    /// <summary>Reads a tag such as "v1.2.0" or "1.2".</summary>
    static bool TryParse(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (!Version.TryParse(tag?.Trim().TrimStart('v', 'V'), out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    // "1.2" and the assembly's "1.2.0.0" both become 1.2.0, so they compare as equal.
    static Version Normalize(Version? v) => v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
}
