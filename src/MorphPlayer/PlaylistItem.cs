using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MorphPlayer;

public sealed class PlaylistItem : INotifyPropertyChanged
{
    internal static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".alac", ".aiff", ".ape", ".mka",
    };

    bool _isCurrent;
    string _title;
    string? _details;
    ImageSource? _thumbnail;
    bool _hasVideo;

    public PlaylistItem(string source)
    {
        Source = source;
        IsUrl = Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile;
        Display = IsUrl ? source : Path.GetFileName(source);
        _title = Display;
        // A guess until the file has been read; it only picks the placeholder icon.
        _hasVideo = IsUrl || !AudioExtensions.Contains(Path.GetExtension(source));
    }

    /// <summary>A local file path or a network URL.</summary>
    public string Source { get; }
    public bool IsUrl { get; }
    /// <summary>The file name (or URL).</summary>
    public string Display { get; }

    /// <summary>Set once the file's tags, length and thumbnail have been looked at, whatever was found.</summary>
    public bool MetadataLoaded { get; set; }
    public long DurationMs { get; set; }
    /// <summary>Local path of the cover art LibVLC extracted, for music.</summary>
    public string? ArtworkPath { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }

    /// <summary>For a web page: the stream yt-dlp found behind it, once asked.</summary>
    public WebStream? Stream { get; set; }
    /// <summary>A URL to hand straight to LibVLC, without asking yt-dlp: it doesn't know the site, or isn't installed.</summary>
    public bool PlayDirectly { get; set; }
    /// <summary>Set once a link's own start time ("&amp;t=58s") has been used; replays start as usual.</summary>
    public bool LinkStartUsed { get; set; }

    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    /// <summary>The track title from the tags, or the file name.</summary>
    public string Title { get => _title; set => Set(ref _title, value); }

    /// <summary>Second line in the playlist: artist and length for music, length for video.</summary>
    public string? Details { get => _details; set => Set(ref _details, value); }

    /// <summary>A frame from the video, or the cover art.</summary>
    public ImageSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }

    public bool HasVideo { get => _hasVideo; set => Set(ref _hasVideo, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
