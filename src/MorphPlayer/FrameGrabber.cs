using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MorphPlayer;

/// <summary>
/// Grabs a single frame from a video file as a bitmap, with a hidden, silent player that decodes into memory.
/// LibVLC 3 has no thumbnail API (that arrives in LibVLC 4), so this is the usual stand-in.
/// One frame at a time; callers queue behind each other.
/// </summary>
sealed class FrameGrabber : IDisposable
{
    readonly LibVLC _libVlc;
    readonly VlcPlayer _player;
    readonly int _maxWidth;
    readonly SemaphoreSlim _gate = new(1, 1);

    // LibVLC calls these from its own threads; the fields keep the delegates alive for as long as it might.
    readonly VlcPlayer.LibVLCVideoFormatCb _formatCb;
    readonly VlcPlayer.LibVLCVideoCleanupCb _cleanupCb;
    readonly VlcPlayer.LibVLCVideoLockCb _lockCb;
    readonly VlcPlayer.LibVLCVideoDisplayCb _displayCb;

    IntPtr _buffer;
    int _width, _height, _pitch;
    TaskCompletionSource<BitmapSource?>? _pending;

    public FrameGrabber(LibVLC libVlc, int maxWidth)
    {
        _libVlc = libVlc;
        _maxWidth = maxWidth;
        _player = new VlcPlayer(libVlc);
        _formatCb = Format;
        _cleanupCb = Cleanup;
        _lockCb = Lock;
        _displayCb = Display;
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);
    }

    /// <summary>The frame at <paramref name="timeMs"/>; null if none came.</summary>
    public async Task<BitmapSource?> GrabAsync(string path, long timeMs, CancellationToken cancel = default, int timeoutMs = 5000) =>
        (await GrabWithArtworkAsync(path, timeMs, waitForArtwork: false, cancel, timeoutMs)).Frame;

    /// <summary>
    /// A frame, plus the file's cover art if LibVLC has a picture file for it. Some covers, such as Matroska
    /// "cover.jpg" attachments, only become a file once the video plays, which this does anyway; with
    /// <paramref name="waitForArtwork"/> it keeps playing a little longer for that if needed.
    /// </summary>
    public async Task<(BitmapSource? Frame, string? ArtworkUrl)> GrabWithArtworkAsync(
        string path, long timeMs, bool waitForArtwork, CancellationToken cancel = default, int timeoutMs = 5000)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            var result = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pending, result);

            using var media = new Media(_libVlc, path, FromType.FromPath);
            media.AddOption(":no-audio");
            media.AddOption(":no-spu");
            // No ":input-fast-seek": that lands on the keyframe before the time asked for, which in testing put previews
            // up to 2.8 s early (and 10 s or more in films with sparse keyframes). Exact seeking decodes on from that
            // keyframe and measured no slower.
            media.AddOption(":avcodec-hw=none"); // setting up GPU decoding costs more than one frame is worth
            if (timeMs > 0) media.AddOption(FormattableString.Invariant($":start-time={timeMs / 1000.0:0.###}"));
            _player.Play(media);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(timeoutMs);
            var finished = await Task.WhenAny(result.Task, Task.Delay(Timeout.Infinite, timeout.Token));
            Volatile.Write(ref _pending, null);

            var artwork = media.Meta(MetadataType.ArtworkURL);
            for (var waited = 0; waitForArtwork && !IsFileUrl(artwork) && waited < 1500 && !cancel.IsCancellationRequested; waited += 100)
            {
                await Task.Delay(100);
                artwork = media.Meta(MetadataType.ArtworkURL);
            }

            await Task.Run(_player.Stop); // Stop waits for LibVLC's threads, so keep it off the UI thread
            return (finished == result.Task ? await result.Task : null, IsFileUrl(artwork) ? artwork : null);
        }
        finally
        {
            _gate.Release();
        }
    }

    static bool IsFileUrl(string? url) => url != null && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    uint Format(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // RV32 is 32-bit BGRX, which WPF reads as Bgr32. Scale down to the size wanted, keeping the shape.
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        var scale = Math.Min(1.0, (double)_maxWidth / Math.Max(1u, width));
        width = (uint)Math.Max(2, Math.Round(width * scale));
        height = (uint)Math.Max(2, Math.Round(height * scale));
        pitches = width * 4;
        lines = height;

        FreeBuffer();
        (_width, _height, _pitch) = ((int)width, (int)height, (int)pitches);
        _buffer = Marshal.AllocHGlobal(_pitch * _height);
        return 1;
    }

    void Cleanup(ref IntPtr opaque) => FreeBuffer();

    IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    void Display(IntPtr opaque, IntPtr picture)
    {
        var pending = Interlocked.Exchange(ref _pending, null);
        if (pending == null || _buffer == IntPtr.Zero) return;
        var pixels = new byte[_pitch * _height];
        Marshal.Copy(_buffer, pixels, 0, pixels.Length);
        var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgr32, null, pixels, _pitch);
        bitmap.Freeze(); // made on LibVLC's thread, shown on the UI thread
        pending.TrySetResult(bitmap);
    }

    void FreeBuffer()
    {
        if (_buffer == IntPtr.Zero) return;
        Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }

    public void Dispose()
    {
        _player.Stop();
        _player.Dispose();
        FreeBuffer();
    }
}
