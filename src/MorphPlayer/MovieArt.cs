using System.Collections.Concurrent;
using System.IO;

namespace MorphPlayer;

/// <summary>Finds a poster image saved beside a video, the way Plex, Kodi and Jellyfin libraries keep them.</summary>
static class MovieArt
{
    static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png" };

    // Named after the video ("Movie.mkv" → "Movie-poster.jpg"): always that video's own.
    static readonly string[] OwnSuffixes = { "-poster", "", "-cover", "-thumb" };

    // Named for the folder: only trusted when the folder holds a single video. In a folder of episodes, poster.jpg is
    // the show's, and each episode's own frame tells them apart better.
    static readonly string[] FolderNames = { "poster", "folder", "cover", "movie" };

    static readonly ConcurrentDictionary<string, int> VideoCounts = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindPosterFile(string videoPath, Func<string, bool> isVideoFile)
    {
        try
        {
            var folder = Path.GetDirectoryName(videoPath);
            if (folder == null) return null;
            var name = Path.GetFileNameWithoutExtension(videoPath);

            var own = FirstExisting(folder, OwnSuffixes.Select(suffix => name + suffix));
            if (own != null) return own;

            var videos = VideoCounts.GetOrAdd(folder, f => Directory.EnumerateFiles(f).Count(isVideoFile));
            return videos == 1 ? FirstExisting(folder, FolderNames) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    static string? FirstExisting(string folder, IEnumerable<string> baseNames) =>
        baseNames.SelectMany(baseName => ImageExtensions.Select(extension => Path.Combine(folder, baseName + extension)))
                 .FirstOrDefault(File.Exists);
}
