using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Win32;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MorphPlayer;

public enum RepeatMode { Off, All, One }

public partial class MainWindow : Window
{
    static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts",
        ".3gp", ".ogv", ".vob", ".divx", ".rm", ".rmvb", ".asf",
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".alac", ".aiff", ".ape", ".mka",
    };
    static HashSet<string> SubtitleExtensions => SubtitleFinder.SubtitleExtensions;
    static readonly float[] Speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f, 4f };
    // LibVLC 3's "stereo-mode" values (vlc_aout.h AOUT_VAR_CHAN_*). LibVLCSharp's enum stops at 5, but
    // libvlc_audio_set_channel passes any value through, so Original, Headphones and Mono are cast. The mode stays
    // set across files; Headphones only adds its binaural mix when a file has more than two channels.
    static readonly (AudioOutputChannel Mode, string Name, string? Tip)[] StereoModes =
    {
        ((AudioOutputChannel)0, "Original", "The channels as recorded"),
        (AudioOutputChannel.Stereo, "Stereo", "Two channels; surround sound is mixed down"),
        ((AudioOutputChannel)7, "Mono", "Every channel mixed into one, heard in both ears"),
        (AudioOutputChannel.Left, "Left Channel Only", null),
        (AudioOutputChannel.Right, "Right Channel Only", null),
        (AudioOutputChannel.RStereo, "Reverse Stereo", "Swaps left and right"),
        ((AudioOutputChannel)6, "Headphones", "For 5.1 and 7.1 sound: a binaural mix so the surround channels still "
                                              + "sound placed around you. Stereo files play as normal."),
    };

    // Resume rules, as in VLC: short files (most songs) always start from the top, and a position near either
    // end isn't worth keeping.
    const long ResumeMinLengthMs = 3 * 60_000;
    const long ResumeMarginMs = 30_000;

    // Seek bar previews are fetched per second of the video, so a frame is never more than half a second from the
    // pointer; the most recent ones are kept for when the pointer comes back.
    const long PreviewResolutionMs = 1000;
    const int PreviewCacheSize = 150;

    // The overlay is nearly transparent while video is on screen; otherwise it covers the
    // native video host, which would show a gray window background.
    static readonly Brush VideoVisibleBackground = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    static readonly Brush NoVideoBackground = Brushes.Black;

    readonly AppState _state = AppState.Load();
    readonly LibVLC _libVlc;
    readonly VlcPlayer _player;
    readonly FrameGrabber _thumbnailGrabber; // playlist thumbnails
    readonly FrameGrabber _previewGrabber;   // frames shown while hovering the seek bar
    readonly List<PlaylistItem> _metadataQueue = new();
    readonly Dictionary<long, BitmapSource> _previewCache = new(); // by second, for the file that's playing
    readonly Queue<long> _previewCacheOrder = new();               // oldest first, to keep the cache bounded
    readonly Dictionary<int, string> _subtitleLabels = new();      // LibVLC track id -> name, for subtitle files we added
    PlaylistItem? _subtitlesPendingFor;                            // look for subtitle files once this starts playing
    readonly ObservableCollection<PlaylistItem> _playlist = new();
    readonly List<PlaylistItem> _history = new();         // play order, for "previous" in shuffle mode
    readonly HashSet<PlaylistItem> _shuffleRound = new();  // items already played in the current shuffle pass
    readonly Random _random = new();
    readonly DispatcherTimer _uiTimer;
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _resumeBannerTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    PlaylistItem? _current;
    RepeatMode _repeat;
    bool _shuffle, _muted, _seeking, _updatingSeekBar, _menuOpen, _fullscreen, _playlistBeforeFullscreen, _overlayHooked;
    bool _metadataRunning, _previewBusy, _closing;
    long? _previewWanted;
    long _previewHoverSecond = -1;
    double _previewX = double.NaN;
    int _ticks;
    DateTime _holdSeekBarUntil;
    float _rate = 1f;
    WindowState _stateBeforeFullscreen;
    Point _lastMouse;

    public MainWindow()
    {
        InitializeComponent();
        TitleBarTheme.Follow(this);

        // SubtitleFinder replaces LibVLC's own subtitle matching, which misses most differently named files.
        _libVlc = new LibVLC("--no-video-title-show", "--no-sub-autodetect-file");
        _player = new VlcPlayer(_libVlc) { EnableKeyInput = false, EnableMouseInput = false };

        // libVLC raises events on its own threads; never call back into the player from there.
        _player.Playing += (_, _) => Dispatcher.InvokeAsync(OnPlaying);
        _player.Paused += (_, _) => Dispatcher.InvokeAsync(OnPausedOrStopped);
        _player.Stopped += (_, _) => Dispatcher.InvokeAsync(OnPausedOrStopped);
        _player.EndReached += (_, _) => Dispatcher.InvokeAsync(OnEndReached);
        _player.EncounteredError += (_, _) => Dispatcher.InvokeAsync(OnError);
        _thumbnailGrabber = new FrameGrabber(_libVlc, maxWidth: 160);
        _previewGrabber = new FrameGrabber(_libVlc, maxWidth: 320);

        PlaylistBox.ItemsSource = _playlist;
        _playlist.CollectionChanged += (_, _) => PlaylistHeader.Text = $"Playlist ({_playlist.Count})";
        VolumeSlider.ValueChanged += Volume_ValueChanged;

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => UpdateTime(), Dispatcher);
        _hideTimer.Tick += (_, _) => HideControls();
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); OsdBorder.Visibility = Visibility.Collapsed; };
        _resumeBannerTimer.Tick += (_, _) => HideResumeBanner();
    }

    // ---------- Startup / shutdown ----------

    void Video_Loaded(object sender, RoutedEventArgs e)
    {
        if (Video.MediaPlayer != null) return;
        Video.MediaPlayer = _player;

        // Files passed on the command line ("Open with…" / drag onto the .exe).
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0) AddToPlaylist(ExpandPaths(args), play: true);
    }

    void Overlay_Loaded(object sender, RoutedEventArgs e)
    {
        // The overlay lives in its own window; when it has focus, keys go there instead of here.
        var overlayWindow = Window.GetWindow(Overlay);
        if (_overlayHooked || overlayWindow == null || overlayWindow == this) return;
        overlayWindow.PreviewKeyDown += OnKeyDown;
        _overlayHooked = true;
    }

    void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _uiTimer.Stop();
        RememberPosition();
        _state.Save();
        _player.Stop();
        _player.Dispose();
        _thumbnailGrabber.Dispose();
        _previewGrabber.Dispose();
        _libVlc.Dispose();
    }

    // ---------- Playlist ----------

    void AddToPlaylist(IEnumerable<string> sources, bool play)
    {
        PlaylistItem? first = null;
        foreach (var source in sources)
        {
            var item = new PlaylistItem(source);
            item.PropertyChanged += Item_PropertyChanged;
            _playlist.Add(item);
            QueueMetadata(item);
            first ??= item;
        }
        if (first != null && (play || _current == null)) PlayItem(first);
    }

    static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(f => MediaExtensions.Contains(Path.GetExtension(f)))
                    .Order(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files) yield return file;
            }
            else
            {
                yield return path; // files of unknown type are still worth a try; libVLC sniffs content
            }
        }
    }

    void PlayItem(PlaylistItem item, bool recordHistory = true)
    {
        RememberPosition(); // of the file being left
        if (_current != null) _current.IsCurrent = false;
        _current = item;
        item.IsCurrent = true;
        if (recordHistory)
        {
            _history.Add(item);
            _shuffleRound.Add(item);
        }

        using var media = item.IsUrl
            ? new Media(_libVlc, new Uri(item.Source))
            : new Media(_libVlc, item.Source, FromType.FromPath);
        ResumePoint? resume = null;
        if (!item.IsUrl && _state.Resume.TryGetValue(ResumeKey(item), out var saved))
        {
            resume = saved;
            // Starting there directly, rather than seeking once playing, avoids a flash of the opening frames.
            media.AddOption(FormattableString.Invariant($":start-time={saved.TimeMs / 1000.0:0.###}"));
        }
        _player.Play(media);
        _state.Save();
        ClearPreviewCache();
        _subtitleLabels.Clear();
        _subtitlesPendingFor = item.IsUrl ? null : item;
        QueueMetadata(item, first: true); // the cover and title of what's playing matter most

        UpdateNowPlaying();
        Hint.Visibility = Visibility.Collapsed;
        PlaylistBox.ScrollIntoView(item);
        if (resume != null)
            ShowResumeBanner(resume.TimeMs);
        else
            HideResumeBanner();
        Osd(item.Title);
    }

    // ---------- Titles, cover art and thumbnails ----------

    void QueueMetadata(PlaylistItem item, bool first = false)
    {
        if (item.IsUrl || item.MetadataLoaded) return; // streams could take minutes to answer, or never
        _metadataQueue.Remove(item);
        if (first)
            _metadataQueue.Insert(0, item);
        else
            _metadataQueue.Add(item);
        if (!_metadataRunning) _ = ProcessMetadataQueue();
    }

    /// <summary>Works through the playlist one file at a time in the background, filling in what it finds.</summary>
    async Task ProcessMetadataQueue()
    {
        _metadataRunning = true;
        try
        {
            while (_metadataQueue.Count > 0 && !_closing)
            {
                var item = _metadataQueue[0];
                _metadataQueue.RemoveAt(0);
                if (item.MetadataLoaded || !_playlist.Contains(item)) continue;
                try
                {
                    await LoadMetadata(item);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // A file LibVLC can't read just keeps its file name and placeholder icon.
                }
                item.MetadataLoaded = true;
            }
        }
        finally
        {
            _metadataRunning = false;
        }
    }

    async Task LoadMetadata(PlaylistItem item)
    {
        using var media = new Media(_libVlc, item.Source, FromType.FromPath);
        if (!await ParseAsync(media, MediaParseOptions.ParseLocal)) return;

        item.DurationMs = Math.Max(0, media.Duration);
        item.HasVideo = media.Tracks.Any(t => t.TrackType == TrackType.Video);
        var length = item.DurationMs > 0 ? Format(item.DurationMs) : null;

        if (item.HasVideo)
        {
            item.Details = length;
            // A movie's own cover wins over a frame: a poster image saved beside it, then one embedded in the file.
            // MP4 covers are already extracted by the parse; Matroska cover attachments only appear as
            // "attachment://cover.jpg" until the file plays, which grabbing a frame does anyway.
            var poster = await Task.Run(() => MovieArt.FindPosterFile(item.Source, IsVideoFile))
                         ?? ArtworkFile(media.Meta(MetadataType.ArtworkURL));
            if (poster != null)
            {
                item.Thumbnail = LoadImage(poster, decodeWidth: 96);
                return;
            }
            var hasAttachedCover = media.Meta(MetadataType.ArtworkURL)?.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase) == true;
            // A tenth of the way in skips black openings and title cards without giving much away.
            var (frame, artwork) = await _thumbnailGrabber.GrabWithArtworkAsync(item.Source, item.DurationMs / 10, waitForArtwork: hasAttachedCover);
            item.Thumbnail = LoadImage(ArtworkFile(artwork), decodeWidth: 96) ?? (ImageSource?)frame;
            return;
        }

        // Video titles are left alone: in downloaded files they're often a release name or a website.
        var title = media.Meta(MetadataType.Title)?.Trim();
        if (!string.IsNullOrEmpty(title) && title != item.Display) item.Title = title; // without a tag, LibVLC reports the file name
        item.Artist = NullIfBlank(media.Meta(MetadataType.Artist));
        item.Album = NullIfBlank(media.Meta(MetadataType.Album));
        item.Details = string.Join(" · ", new[] { item.Artist, length }.Where(s => s != null));

        // Cover art is a second pass, for music only (videos get a frame instead): FetchLocal looks for art embedded
        // in the file or a cover.jpg / folder.jpg beside it.
        using var withArt = new Media(_libVlc, item.Source, FromType.FromPath);
        if (await ParseAsync(withArt, MediaParseOptions.ParseLocal | MediaParseOptions.FetchLocal)
            && ArtworkFile(withArt.Meta(MetadataType.ArtworkURL)) is { } art)
        {
            item.ArtworkPath = art;
            item.Thumbnail = LoadImage(art, decodeWidth: 96);
        }
    }

    /// <summary>The local file behind a LibVLC artwork URL, if it is one and still exists.</summary>
    static string? ArtworkFile(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath) ? uri.LocalPath : null;

    static bool IsVideoFile(string path)
    {
        var extension = Path.GetExtension(path);
        return MediaExtensions.Contains(extension) && !PlaylistItem.AudioExtensions.Contains(extension);
    }

    /// <summary>
    /// Parses with a hard limit on top of LibVLC's own timeout, which covers reading the file but not the art fetch:
    /// a file that never answers is stopped instead of stalling the queue.
    /// </summary>
    static async Task<bool> ParseAsync(Media media, MediaParseOptions options)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            return await media.Parse(options, timeout: 5000, limit.Token) == MediaParsedStatus.Done;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    static BitmapImage? LoadImage(string? path, int decodeWidth)
    {
        if (path == null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = decodeWidth; // cover art is often 1000+ pixels; no need to keep it all
            image.CacheOption = BitmapCacheOption.OnLoad; // read now, so the file isn't held open
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender == _current && e.PropertyName is nameof(PlaylistItem.Title) or nameof(PlaylistItem.Details)
                                                    or nameof(PlaylistItem.Thumbnail))
            UpdateNowPlaying();
    }

    /// <summary>The window title, the name in the control bar, and the cover and artist shown for music.</summary>
    void UpdateNowPlaying()
    {
        if (_current is not { } item) return;
        Title = $"{item.Title} - MorphPlayer";
        TitleText.Text = item.Title;
        AudioDetails.Text = string.Join(" — ", new[] { item.Artist, item.Album }.Where(s => s != null));
        AudioDetails.Visibility = AudioDetails.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var cover = LoadImage(item.ArtworkPath, decodeWidth: 600);
        CoverImage.Source = cover;
        CoverFrame.Visibility = cover != null ? Visibility.Visible : Visibility.Collapsed;
        MusicGlyph.Visibility = cover != null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Seek bar preview ----------

    void Seek_MouseMove(object sender, MouseEventArgs e)
    {
        var length = _player.Length;
        if (_current == null || length <= 0)
        {
            HideSeekPreview();
            return;
        }

        var x = e.GetPosition(SeekSlider).X;
        // WPF repeats mouse moves whenever the bar redraws (four times a second while playing), and a resting hand
        // wobbles a pixel or two. Neither should change the preview.
        if (SeekPreview.Visibility == Visibility.Visible && Math.Abs(x - _previewX) < 3) return;
        _previewX = x;

        const double thumbHalf = 6; // the track runs between the thumb's centre at either end
        var fraction = Math.Clamp((x - thumbHalf) / Math.Max(1, SeekSlider.ActualWidth - 2 * thumbHalf), 0, 1);
        var time = (long)(fraction * length);
        SeekPreviewTime.Text = Format(time);

        if (_player.VideoTrackCount > 0 && !_current.IsUrl)
        {
            _previewHoverSecond = (time + PreviewResolutionMs / 2) / PreviewResolutionMs;
            if (_previewCache.TryGetValue(_previewHoverSecond, out var frame))
                ShowPreviewFrame(frame);
            else
                RequestPreviewFrame(_previewHoverSecond);
        }
        else
        {
            _previewHoverSecond = -1;
            SeekPreviewImage.Visibility = Visibility.Collapsed; // music: just the time
        }

        SeekPreview.Visibility = Visibility.Visible;
        // Centred over the pointer, kept inside the window, just above the seek bar.
        SeekPreview.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // DesiredSize includes the margin, which holds the box's last position; left in, it dragged the box back
        // toward the middle the further right the pointer went.
        var width = SeekPreview.DesiredSize.Width - SeekPreview.Margin.Left - SeekPreview.Margin.Right;
        var anchor = SeekSlider.TranslatePoint(new Point(x, 0), Overlay);
        var left = Math.Clamp(anchor.X - width / 2, 4, Math.Max(4, Overlay.ActualWidth - width - 4));
        SeekPreview.Margin = new Thickness(left, 0, 0, Overlay.ActualHeight - anchor.Y + 4);
    }

    void Seek_MouseLeave(object sender, MouseEventArgs e) => HideSeekPreview();

    void HideSeekPreview()
    {
        SeekPreview.Visibility = Visibility.Collapsed;
        _previewWanted = null;
        _previewHoverSecond = -1;
        _previewX = double.NaN;
    }

    void ClearPreviewCache()
    {
        _previewCache.Clear();
        _previewCacheOrder.Clear();
    }

    void ShowPreviewFrame(BitmapSource frame)
    {
        SeekPreviewImage.Source = frame;
        SeekPreviewImage.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Fetches frames one at a time, always for the latest second hovered: moving along the bar skips the points
    /// passed over instead of queueing them. Until it arrives, the previous frame stays up.
    /// </summary>
    async void RequestPreviewFrame(long second)
    {
        _previewWanted = second;
        if (_previewBusy) return;
        _previewBusy = true;
        try
        {
            while (_previewWanted is long wanted && !_closing)
            {
                _previewWanted = null;
                var item = _current;
                if (item == null || _previewCache.ContainsKey(wanted)) continue;
                var frame = await _previewGrabber.GrabAsync(item.Source, wanted * PreviewResolutionMs, timeoutMs: 3000);
                if (frame == null || item != _current) continue;
                _previewCache[wanted] = frame;
                _previewCacheOrder.Enqueue(wanted);
                while (_previewCacheOrder.Count > PreviewCacheSize) _previewCache.Remove(_previewCacheOrder.Dequeue());
                if (wanted == _previewHoverSecond && SeekPreview.Visibility == Visibility.Visible) ShowPreviewFrame(frame);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A preview is a nicety; never let one take the player down.
        }
        finally
        {
            _previewBusy = false;
        }
    }

    // ---------- Resume ----------

    static string ResumeKey(PlaylistItem item)
    {
        try { return Path.GetFullPath(item.Source); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return item.Source; }
    }

    /// <summary>Saves (in memory) where the current file is, or forgets it if that's too close to either end.</summary>
    void RememberPosition()
    {
        if (_current is not { IsUrl: false } item || _player.State is not (VLCState.Playing or VLCState.Paused)) return;
        var time = _player.Time;
        var length = _player.Length;
        if (length >= ResumeMinLengthMs && time >= ResumeMarginMs && time <= length - ResumeMarginMs)
            _state.Resume[ResumeKey(item)] = new ResumePoint(time, length, DateTime.UtcNow);
        else
            _state.Resume.Remove(ResumeKey(item));
    }

    void ForgetPosition(PlaylistItem item)
    {
        if (!item.IsUrl && _state.Resume.Remove(ResumeKey(item))) _state.Save();
    }

    void ShowResumeBanner(long timeMs)
    {
        ResumeText.Text = $"Resumed at {Format(timeMs)}";
        ResumeBanner.Visibility = Visibility.Visible;
        _resumeBannerTimer.Stop();
        _resumeBannerTimer.Start();
    }

    void HideResumeBanner()
    {
        _resumeBannerTimer.Stop();
        ResumeBanner.Visibility = Visibility.Collapsed;
    }

    void StartOver_Click(object sender, RoutedEventArgs e)
    {
        HideResumeBanner();
        if (_current == null) return;
        ForgetPosition(_current);
        _player.Time = 0;
        Osd("Playing from the start");
    }

    PlaylistItem? NextItem(bool wrap)
    {
        if (_playlist.Count == 0) return null;

        if (_shuffle)
        {
            var candidates = _playlist.Where(p => !_shuffleRound.Contains(p)).ToList();
            if (candidates.Count == 0)
            {
                if (!wrap) return null;
                _shuffleRound.Clear();
                candidates = _playlist.Where(p => p != _current).ToList();
                if (candidates.Count == 0) candidates.Add(_playlist[0]);
            }
            return candidates[_random.Next(candidates.Count)];
        }

        var index = _current == null ? -1 : _playlist.IndexOf(_current);
        if (index + 1 < _playlist.Count) return _playlist[index + 1];
        return wrap ? _playlist[0] : null;
    }

    void PlayNext()
    {
        var next = NextItem(wrap: _repeat == RepeatMode.All);
        if (next != null) PlayItem(next);
    }

    void PlayPrevious()
    {
        if (_current == null) return;

        // Like VLC: a few seconds into a track, "previous" restarts it.
        if (_player.Time > 3000 && _player.IsSeekable)
        {
            _player.Time = 0;
            return;
        }

        if (_shuffle)
        {
            if (_history.Count < 2) return;
            _history.RemoveAt(_history.Count - 1);
            PlayItem(_history[^1], recordHistory: false);
            return;
        }

        var index = _playlist.IndexOf(_current);
        if (index > 0) PlayItem(_playlist[index - 1]);
        else if (_repeat == RepeatMode.All && _playlist.Count > 0) PlayItem(_playlist[^1]);
    }

    void RemoveSelected()
    {
        foreach (var item in PlaylistBox.SelectedItems.Cast<PlaylistItem>().ToList())
        {
            _playlist.Remove(item);
            _shuffleRound.Remove(item);
            _history.RemoveAll(h => h == item);
        }
    }

    void ClearPlaylist()
    {
        RememberPosition();
        _state.Save();
        HideResumeBanner();
        _player.Stop();
        _playlist.Clear();
        _metadataQueue.Clear();
        ClearPreviewCache();
        _history.Clear();
        _shuffleRound.Clear();
        _current = null;
        Title = "MorphPlayer";
        TitleText.Text = "";
        Hint.Visibility = Visibility.Visible;
        ResetTimeline();
    }

    // ---------- Player events ----------

    void OnPlaying()
    {
        // Audio output is (re)created per media, so re-apply audio settings and speed.
        _player.Volume = (int)VolumeSlider.Value;
        _player.Mute = _muted;
        if (_player.Rate != _rate) _player.SetRate(_rate);
        if (_state.StereoMode != 0 && (int)_player.Channel != _state.StereoMode)
            _player.SetChannel((AudioOutputChannel)_state.StereoMode);
        if (!string.IsNullOrEmpty(_state.AudioDevice) && _player.OutputDevice != _state.AudioDevice)
            _player.SetOutputDevice(_state.AudioDevice);
        if (_subtitlesPendingFor is { } item && item == _current)
        {
            _subtitlesPendingFor = null;
            _ = AttachMatchingSubtitles(item);
        }
        UpdatePlayButton();
        ShowControls();
    }

    void OnPausedOrStopped()
    {
        UpdatePlayButton();
        ShowControls();
    }

    void OnEndReached()
    {
        if (_current == null) return;
        ForgetPosition(_current); // watched to the end: next time starts from the top
        HideResumeBanner();
        if (_repeat == RepeatMode.One)
        {
            PlayItem(_current, recordHistory: false);
            return;
        }

        var next = NextItem(wrap: _repeat == RepeatMode.All);
        if (next != null)
        {
            PlayItem(next);
        }
        else
        {
            _player.Stop();
            ResetTimeline();
        }
    }

    void OnError()
    {
        Osd($"Can't play {_current?.Display}");
        UpdatePlayButton();
        ShowControls();
    }

    // ---------- Transport ----------

    void TogglePlay()
    {
        if (_current == null)
        {
            if (_playlist.Count > 0) PlayItem(_playlist[0]);
            else OpenFiles();
            return;
        }

        if (_player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error or VLCState.NothingSpecial)
            PlayItem(_current, recordHistory: false);
        else
            _player.Pause(); // toggles
    }

    void Stop()
    {
        RememberPosition();
        _state.Save();
        HideResumeBanner();
        _player.Stop();
        ResetTimeline();
    }

    void SeekBy(long ms)
    {
        if (!_player.IsSeekable) return;
        var length = _player.Length;
        var target = Math.Max(0, _player.Time + ms);
        if (length > 0) target = Math.Min(target, length - 500);
        _player.Time = target;
        Osd($"{(ms > 0 ? "+" : "−")}{Math.Abs(ms) / 1000}s  ({Format(target)} / {Format(length)})");
    }

    void ChangeVolume(int delta) => VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);

    void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_muted) ToggleMute();
        _player.Volume = (int)e.NewValue;
        UpdateMuteButton();
        Osd($"Volume {(int)e.NewValue}%");
    }

    void ToggleMute()
    {
        _muted = !_muted;
        _player.Mute = _muted;
        UpdateMuteButton();
        Osd(_muted ? "Muted" : $"Volume {(int)VolumeSlider.Value}%");
    }

    void SetSpeed(float rate)
    {
        _rate = rate;
        _player.SetRate(rate);
        SpeedButton.Content = $"{rate:0.##}x";
        Osd($"Speed {rate:0.##}x");
    }

    void StepSpeed(int direction)
    {
        var next = direction > 0
            ? Speeds.FirstOrDefault(s => s > _rate + 0.001f, Speeds[^1])
            : Speeds.LastOrDefault(s => s < _rate - 0.001f, Speeds[0]);
        SetSpeed(next);
    }

    // ---------- Tracks & subtitles ----------

    /// <summary>Adds the subtitle files that belong to the file that just started, and turns on the best one.</summary>
    async Task AttachMatchingSubtitles(PlaylistItem item)
    {
        IReadOnlyList<SubtitleMatch> matches;
        try
        {
            matches = await Task.Run(() => SubtitleFinder.Find(item.Source, IsVideoFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        // As VLC does, one is shown straight away: full subtitles before forced-only ones, then subtitles in the
        // Windows display language, then the closest match by name.
        var language = CultureInfo.CurrentUICulture;
        while (!language.IsNeutralCulture && !language.Parent.Equals(CultureInfo.InvariantCulture)) language = language.Parent;
        var best = matches
            .OrderBy(m => m.Forced)
            .ThenByDescending(m => m.Language?.StartsWith(language.EnglishName.Split(" (")[0], StringComparison.OrdinalIgnoreCase) == true)
            .ThenByDescending(m => m.Score)
            .FirstOrDefault();
        foreach (var match in matches)
        {
            var id = await AttachSubtitle(item, match);
            if (id != null && match == best) _player.SetSpu(id.Value);
            if (item != _current) return;
        }
    }

    /// <summary>Adds a subtitle file to what's playing and returns LibVLC's id for its track.</summary>
    async Task<int?> AttachSubtitle(PlaylistItem item, SubtitleMatch match)
    {
        if (item != _current) return null;
        var before = _player.SpuDescription.Select(t => t.Id).ToHashSet();
        if (!_player.AddSlave(MediaSlaveType.Subtitle, new Uri(match.Path).AbsoluteUri, select: false)) return null;
        // LibVLC adds the track a moment later and names it only "Track n"; spotting its id is how our name gets on it.
        for (var waited = 0; waited < 1500; waited += 50)
        {
            await Task.Delay(50);
            if (item != _current) return null;
            foreach (var track in _player.SpuDescription)
            {
                if (track.Id == -1 || before.Contains(track.Id)) continue;
                _subtitleLabels[track.Id] = match.Label;
                return track.Id;
            }
        }
        return null;
    }

    async void AddSubtitle(string path)
    {
        if (_current is not { } item) return;
        try
        {
            var match = SubtitleFinder.Describe(path, item.IsUrl ? null : item.Source);
            if (await AttachSubtitle(item, match) is int id) _player.SetSpu(id);
            Osd($"Subtitle: {match.Label}");
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException)
        {
            Osd($"Can't open {Path.GetFileName(path)}");
        }
    }

    string SubtitleName(TrackDescription track) => _subtitleLabels.TryGetValue(track.Id, out var label) ? label : track.Name;

    void CycleSubtitle() => CycleTrack(_player.SpuDescription, _player.Spu, id => _player.SetSpu(id), "Subtitle", SubtitleName);

    void CycleAudioTrack() => CycleTrack(_player.AudioTrackDescription, _player.AudioTrack, id => _player.SetAudioTrack(id), "Audio", t => t.Name);

    void CycleTrack(TrackDescription[] tracks, int currentId, Func<int, bool> select, string label, Func<TrackDescription, string> nameOf)
    {
        if (tracks.Length == 0)
        {
            Osd($"{label}: none available");
            return;
        }
        var index = Array.FindIndex(tracks, t => t.Id == currentId);
        var next = tracks[(index + 1) % tracks.Length];
        select(next.Id);
        Osd($"{label}: {nameOf(next)}");
    }

    // ---------- Audio output ----------

    void SetStereoMode(AudioOutputChannel mode, string name)
    {
        _state.StereoMode = (int)mode;
        _state.Save();
        // LibVLC restarts its audio output to apply it, which takes a moment. It fails only when there's no audio
        // output yet; OnPlaying applies the saved mode then.
        _player.SetChannel(mode);
        Osd($"Stereo mode: {name}");
    }

    void SetAudioDevice(string? id, string name)
    {
        _state.AudioDevice = string.IsNullOrEmpty(id) ? null : id;
        _state.Save();
        _player.SetOutputDevice(id ?? "");
        Osd($"Audio device: {name}");
    }

    // ---------- Menus ----------

    void ShowMenu(Button anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Top;
        menu.Opened += Menu_Opened;
        menu.Closed += Menu_Closed;
        menu.IsOpen = true;
    }

    static MenuItem Choice(string header, bool isChecked, Action onClick)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        return item;
    }

    static MenuItem Unavailable(string header) => new() { Header = header, IsEnabled = false };

    void AddTrackItems(ItemCollection items, TrackDescription[] tracks, int currentId, Func<int, bool> select, string label,
                       Func<TrackDescription, string> nameOf)
    {
        if (tracks.Length == 0)
            items.Add(Unavailable(_current == null ? "Nothing playing" : "No tracks in this file"));
        foreach (var track in tracks)
        {
            var (id, name) = (track.Id, nameOf(track));
            items.Add(Choice(name, id == currentId, () => { select(id); Osd($"{label}: {name}"); }));
        }
    }

    void AddAudioTrackItems(ItemCollection items) =>
        AddTrackItems(items, _player.AudioTrackDescription, _player.AudioTrack, id => _player.SetAudioTrack(id), "Audio", t => t.Name);

    void AddSubtitleItems(ItemCollection items)
    {
        var add = new MenuItem { Header = "Add Subtitle File…", IsEnabled = _current != null };
        add.Click += (_, _) => PromptSubtitle();
        items.Add(add);
        items.Add(new Separator());
        AddTrackItems(items, _player.SpuDescription, _player.Spu, id => _player.SetSpu(id), "Subtitle", SubtitleName);
    }

    void AddAudioDeviceItems(ItemCollection items)
    {
        var devices = _player.AudioOutputDeviceEnum;
        var selected = _state.AudioDevice ?? "";
        // LibVLC normally lists the Windows default itself, with an empty id.
        if (!devices.Any(d => string.IsNullOrEmpty(d.DeviceIdentifier)))
            items.Add(Choice("Default", selected == "", () => SetAudioDevice(null, "Default")));
        foreach (var device in devices)
        {
            var (id, name) = (device.DeviceIdentifier ?? "", device.Description);
            items.Add(Choice(name, id == selected, () => SetAudioDevice(id, name)));
        }
    }

    void AddStereoModeItems(ItemCollection items)
    {
        foreach (var (mode, name, tip) in StereoModes)
        {
            var item = Choice(name, (int)mode == _state.StereoMode, () => SetStereoMode(mode, name));
            item.ToolTip = tip;
            items.Add(item);
        }
    }

    MenuItem Submenu(string header, Action<ItemCollection> fill)
    {
        var menu = new MenuItem { Header = header };
        fill(menu.Items);
        return menu;
    }

    /// <summary>The right-click menu's submenus describe the file that's playing, so they're rebuilt each time.</summary>
    void OverlayMenu_Opened(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        RefillMenu(AudioTrackMenu, AddAudioTrackItems);
        RefillMenu(SubtitleMenu, AddSubtitleItems);
        RefillMenu(AudioDeviceMenu, AddAudioDeviceItems);
        RefillMenu(StereoModeMenu, AddStereoModeItems);
    }

    static void RefillMenu(MenuItem menu, Action<ItemCollection> fill)
    {
        menu.Items.Clear();
        fill(menu.Items);
    }

    // ---------- UI state ----------

    void UpdateTime()
    {
        UpdateSurface();
        if (_current == null) return;
        // Every 15 seconds, so a crash or power cut loses at most that much of the resume position.
        if (++_ticks % 60 == 0 && _player.IsPlaying)
        {
            RememberPosition();
            _state.Save();
        }
        var length = _player.Length;
        SeekSlider.IsEnabled = _player.IsSeekable;
        LengthText.Text = length > 0 ? Format(length) : "--:--";
        if (_seeking || DateTime.UtcNow < _holdSeekBarUntil) return;
        SetSeekBar(Math.Max(0, _player.Position));
        TimeText.Text = Format(_player.Time);
    }

    void ResetTimeline()
    {
        SetSeekBar(0);
        TimeText.Text = "0:00";
        UpdatePlayButton();
        UpdateSurface();
        ShowControls();
    }

    void UpdateSurface()
    {
        var active = _current != null && _player.State is VLCState.Opening or VLCState.Buffering or VLCState.Playing or VLCState.Paused;
        var hasVideo = active && _player.VideoTrackCount > 0;
        var background = hasVideo ? VideoVisibleBackground : NoVideoBackground;
        if (Overlay.Background != background) Overlay.Background = background;
        AudioInfo.Visibility = active && !hasVideo && _player.State != VLCState.Opening ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdatePlayButton() => PlayButton.Content = _player.State == VLCState.Playing ? "\uE769" : "\uE768";

    void UpdateMuteButton() => MuteButton.Content = _muted || VolumeSlider.Value == 0 ? "\uE74F" : "\uE767";

    void Osd(string text)
    {
        OsdText.Text = text;
        OsdBorder.Visibility = Visibility.Visible;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    void ShowControls()
    {
        ControlBar.Visibility = Visibility.Visible;
        Overlay.Cursor = null;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    void HideControls()
    {
        _hideTimer.Stop();
        if (!_player.IsPlaying || _seeking || _menuOpen || ControlBar.IsMouseOver) return;
        ControlBar.Visibility = Visibility.Hidden;
        if (_fullscreen) Overlay.Cursor = Cursors.None;
    }

    void ToggleFullscreen() => SetFullscreen(!_fullscreen);

    void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        if (on)
        {
            _stateBeforeFullscreen = WindowState;
            _playlistBeforeFullscreen = PlaylistPanel.Visibility == Visibility.Visible;
            PlaylistPanel.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // must leave Maximized first so the borderless window covers the taskbar
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullscreen;
            if (_playlistBeforeFullscreen) PlaylistPanel.Visibility = Visibility.Visible;
        }
        FullscreenButton.Content = on ? "\uE73F" : "\uE740";
        ShowControls();
    }

    void TogglePlaylist()
    {
        if (_fullscreen) SetFullscreen(false);
        PlaylistPanel.Visibility = PlaylistPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ---------- Dialogs ----------

    void OpenFiles()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = $"Media files|{string.Join(";", MediaExtensions.Select(x => "*" + x))}|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) AddToPlaylist(dialog.FileNames, play: true);
    }

    void OpenFolder()
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true) AddToPlaylist(ExpandPaths([dialog.FolderName]), play: true);
    }

    void OpenUrl()
    {
        var url = UrlDialog.Prompt(this);
        if (url != null) AddToPlaylist([url], play: true);
    }

    void PromptSubtitle()
    {
        if (_current == null) return;
        var dialog = new OpenFileDialog
        {
            Filter = $"Subtitles|{string.Join(";", SubtitleExtensions.Select(x => "*" + x))}|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) AddSubtitle(dialog.FileName);
    }

    // ---------- Input ----------

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.O: OpenFiles(); break;
                case Key.F: OpenFolder(); break;
                case Key.N: OpenUrl(); break;
                case Key.L: TogglePlaylist(); break;
                case Key.Left: SeekBy(-60_000); break;
                case Key.Right: SeekBy(60_000); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.S: Stop(); break;
            case Key.N: PlayNext(); break;
            case Key.P: PlayPrevious(); break;
            case Key.Left: SeekBy(-10_000); break;
            case Key.Right: SeekBy(10_000); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M: ToggleMute(); break;
            case Key.F or Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: SetFullscreen(false); break;
            case Key.OemOpenBrackets: StepSpeed(-1); break;
            case Key.OemCloseBrackets: StepSpeed(1); break;
            case Key.OemPlus: SetSpeed(1f); break;
            case Key.V: CycleSubtitle(); break;
            case Key.B: CycleAudioTrack(); break;
            case Key.Delete: RemoveSelected(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(Overlay);
        if (position == _lastMouse) return; // WPF sends spurious moves on layout changes
        _lastMouse = position;
        ShowControls();
    }

    void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !ControlBar.IsMouseOver) ToggleFullscreen();
    }

    void Overlay_MouseWheel(object sender, MouseWheelEventArgs e) => ChangeVolume(e.Delta > 0 ? 5 : -5);

    void Overlay_Drop(object sender, DragEventArgs e) => HandleDrop(e, play: true);

    void Playlist_Drop(object sender, DragEventArgs e) => HandleDrop(e, play: false);

    void HandleDrop(DragEventArgs e, bool play)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            if (paths.Length == 1 && SubtitleExtensions.Contains(Path.GetExtension(paths[0])) && _current != null)
                AddSubtitle(paths[0]);
            else
                AddToPlaylist(ExpandPaths(paths), play);
        }
        else if (e.Data.GetData(DataFormats.Text) is string text
                 && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            AddToPlaylist([text.Trim()], play);
        }
    }

    // Any change to the seek bar that SetSeekBar didn't make came from the user: a click on the track (the
    // Slider moves there itself and swallows the mouse event) or a drag of the thumb.
    void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeekBar || !_player.IsSeekable) return;
        _player.Position = (float)e.NewValue; // live scrubbing while dragging
        TimeText.Text = Format((long)(e.NewValue * _player.Length));
        // LibVLC takes a moment to report the new position; until then the timer would pull the bar back.
        _holdSeekBarUntil = DateTime.UtcNow.AddMilliseconds(600);
    }

    void Seek_DragStarted(object sender, DragStartedEventArgs e) => _seeking = true;

    void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _seeking = false;
        _holdSeekBarUntil = DateTime.UtcNow.AddMilliseconds(600);
    }

    void SetSeekBar(double position)
    {
        _updatingSeekBar = true;
        SeekSlider.Value = position;
        _updatingSeekBar = false;
    }

    void Menu_Opened(object sender, RoutedEventArgs e) => _menuOpen = true;

    void Menu_Closed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        ShowControls();
    }

    // ---------- Button handlers ----------

    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void Stop_Click(object sender, RoutedEventArgs e) => Stop();
    void Prev_Click(object sender, RoutedEventArgs e) => PlayPrevious();
    void Next_Click(object sender, RoutedEventArgs e) => PlayNext();
    void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    void Playlist_Click(object sender, RoutedEventArgs e) => TogglePlaylist();
    void OpenFiles_Click(object sender, RoutedEventArgs e) => OpenFiles();
    void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder();
    void OpenUrl_Click(object sender, RoutedEventArgs e) => OpenUrl();
    void AddFiles_Click(object sender, RoutedEventArgs e) => OpenFiles();
    void RemoveSelected_Click(object sender, RoutedEventArgs e) => RemoveSelected();
    void ClearPlaylist_Click(object sender, RoutedEventArgs e) => ClearPlaylist();

    void Playlist_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistBox.SelectedItem is PlaylistItem item) PlayItem(item);
    }

    void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle;
        _shuffleRound.Clear();
        if (_current != null) _shuffleRound.Add(_current);
        ShuffleButton.Foreground = (Brush)FindResource(_shuffle ? "Accent" : "Fg");
        Osd(_shuffle ? "Shuffle on" : "Shuffle off");
    }

    void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _repeat = (RepeatMode)(((int)_repeat + 1) % 3);
        RepeatButton.Content = _repeat == RepeatMode.One ? "\uE8ED" : "\uE8EE";
        RepeatButton.Foreground = (Brush)FindResource(_repeat == RepeatMode.Off ? "Fg" : "Accent");
        var text = _repeat switch { RepeatMode.All => "all", RepeatMode.One => "one", _ => "off" };
        RepeatButton.ToolTip = $"Repeat: {text}";
        Osd($"Repeat: {text}");
    }

    void Speed_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var speed in Speeds)
        {
            var item = new MenuItem { Header = $"{speed:0.##}x", IsCheckable = true, IsChecked = Math.Abs(speed - _rate) < 0.001f };
            item.Click += (_, _) => SetSpeed(speed);
            menu.Items.Add(item);
        }
        ShowMenu(SpeedButton, menu);
    }

    void AudioTracks_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddAudioTrackItems(menu.Items);
        menu.Items.Add(new Separator());
        menu.Items.Add(Submenu("Audio Device", AddAudioDeviceItems));
        menu.Items.Add(Submenu("Stereo Mode", AddStereoModeItems));
        ShowMenu(AudioButton, menu);
    }

    void Subtitles_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddSubtitleItems(menu.Items);
        ShowMenu(SubtitleButton, menu);
    }
}
