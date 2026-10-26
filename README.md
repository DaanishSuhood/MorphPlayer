# MorphPlayer

**MorphPlayer** is a Windows media player written in C# and WPF on top of
[LibVLC](https://www.videolan.org/vlc/libvlc.html), the playback engine behind VLC.

Because the decoding is done by LibVLC, it plays practically anything VLC plays: MKV, MP4, AVI, WebM,
MOV, FLAC, MP3, Opus, network streams and more, with no codec packs to install. Around that engine it
puts a dark, minimal window with an auto-hiding control bar, a playlist panel, subtitle and audio
track switching, speed control and keyboard shortcuts for everything.

---

## Download

From the [latest release](https://github.com/DaanishSuhood/MorphPlayer/releases/latest), pick **one**:

| File | What it is |
| --- | --- |
| `MorphPlayer-Setup-<version>.exe` | **Installer.** Installs for you alone (or for all users, if you choose), adds Start menu and desktop shortcuts, offers MorphPlayer under **Open with** for media files, and adds an uninstaller in Settings → Apps. No admin rights needed. |
| `MorphPlayer-<version>-portable.zip` | **Portable version.** Unzip it anywhere, such as a USB stick, and run `MorphPlayer\MorphPlayer.exe`. Nothing is installed; to remove it, delete the folder. |

Both need Windows 10 or 11 (x64) and nothing else: no .NET install and no VLC. MorphPlayer is not
code-signed, so SmartScreen may warn you the first time; click **More info → Run anyway**. Each release
lists SHA-256 checksums to check your download against.

Windows never lets an installer make itself the default player. To open videos in MorphPlayer by
double-clicking, go to **Settings → Apps → Default apps → MorphPlayer** and pick the file types.

---

## Building from source

| Needed | Why | How to get it |
| --- | --- | --- |
| **Windows 10 or 11** | WPF desktop app | — |
| **.NET 10 SDK** | Builds and runs the project | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0) |
| LibVLC 3.0 | The playback engine | Restored automatically from NuGet (`VideoLAN.LibVLC.Windows`) |
| LibVLCSharp | .NET bindings and the WPF `VideoView` | Restored automatically from NuGet |
| Inno Setup 6 | Only for building the installer | `winget install --id JRSoftware.InnoSetup -e` |

```
git clone https://github.com/DaanishSuhood/MorphPlayer.git
cd MorphPlayer
dotnet run --project src\MorphPlayer
```

Then drop a file onto the window, or press **Ctrl+O**.

You can also pass files or folders on the command line, which is what makes **Open with…** work:

```
MorphPlayer.exe "C:\Videos\film.mkv" "C:\Music\Album"
```

A build from source has no LibVLC plugin cache, so its first launch can take 20 seconds or more while
LibVLC loads and Windows scans all ~320 plugin DLLs. Release builds include the cache (see below) and
start in about a second.

### Building a release

```
.\publish.ps1                  # portable zip + installer + SHA256SUMS.txt in artifacts\
.\publish.ps1 -SkipInstaller   # portable zip only
```

`publish.ps1` publishes a self-contained single-file `MorphPlayer.exe` for win-x64, with LibVLC in a
`libvlc\` folder beside it (LibVLC loads its plugins from disk, so it can't go inside the exe). It then
downloads the official VLC build of the same version from download.videolan.org, checks it against
VideoLAN's published SHA-256, and runs its `vlc-cache-gen.exe` to write `plugins.dat`. With that cache
LibVLC loads only the plugins a file needs (about 90 for an MP4) instead of all 321 on every start;
in testing, playback began after 0.2 seconds instead of 17. Finally it zips the folder, builds the
installer with Inno Setup, and writes the checksums.

---

## The window

### Video area

| Action | Result |
| --- | --- |
| **Double-click** | Toggles fullscreen |
| **Mouse wheel** | Volume |
| **Right-click** | Menu: open files, folder or stream; the file's audio and subtitle tracks; audio device and stereo mode; playlist; fullscreen |
| **Drop files or folders** | Added to the playlist, and the first one starts playing |
| **Drop a subtitle file** | Loaded onto the video that is playing |

The control bar sits over the bottom of the video and hides itself after 2.5 seconds of no mouse
movement while something is playing. In fullscreen the cursor hides with it. It stays up while paused,
while a menu is open, or while the mouse is over it.

Short messages (volume, speed, track changes, the title of each new file) appear briefly in the top-left
corner.

Music shows its **cover art**, title, artist and album instead of an empty black area: art embedded in the
file, or a `cover.jpg` / `folder.jpg` in the same folder. Without any, it shows a music note.

### Control bar

| Control | Notes |
| --- | --- |
| **Seek bar** | Click anywhere to jump there, or drag to scrub. Hovering shows the time there and, for video, a preview frame. Disabled for live streams that cannot seek |
| **Play / Stop / Previous / Next** | **Previous** restarts the current file if it is more than 3 seconds in, as in VLC |
| **Shuffle** | Plays every item once in random order before repeating any |
| **Repeat** | Cycles off → all → one. Highlighted when on |
| **Speed** (`1x`) | 0.25× to 4×. Pitch is preserved |
| **Audio** (♪) | Lists the file's audio tracks, plus **Audio Device** and **Stereo Mode** |
| **Subtitles** (CC) | Lists the file's subtitle tracks, plus **Add Subtitle File…** |
| **Open file / Open stream / Playlist** | Same as the keyboard shortcuts below |
| **Mute + volume** | 0–100% |
| **Fullscreen** | Hides the window frame and playlist and covers the taskbar |

### Playlist panel

Toggle it with **Ctrl+L** or the playlist button. Double-click an item to play it; **Delete** removes the
selected items; the buttons at the top add files, remove the selection, or clear the list (which also
stops playback). Dropping files onto the panel appends them **without** interrupting what is playing.

Opening a folder adds every media file inside it, including subfolders, sorted by name.

Each entry shows a **thumbnail** and its length. For a video or movie that's the first of these it finds:

1. **A poster saved beside it**, the way Plex, Kodi and Jellyfin keep them: `Movie-poster.jpg`, `Movie.jpg`,
   `Movie-cover.jpg` or `Movie-thumb.jpg` for a file called `Movie.mkv` (`.png` works too). `poster.jpg`,
   `folder.jpg`, `cover.jpg` and `movie.jpg` count only when the folder holds that one video: in a folder of
   episodes they're the show's poster, and each episode's own frame tells them apart better.
2. **A cover embedded in the file**: an MP4's cover tag, or a Matroska `cover.jpg` attachment (as added by
   MakeMKV or mkvtoolnix).
3. **A frame** from a tenth of the way in, past black openings and title cards.

Music shows its cover art and its tagged title and artist. Thumbnails fill in one by one in the background, the
playing file first; a video takes about a third of a second. Network streams keep their URL and an icon.

LibVLC saves covers it extracts from files in `%APPDATA%\vlc\art`, a cache it shares with VLC.

### Seek bar previews

Hovering over the seek bar of a video shows the frame at that point, YouTube-style, to the nearest second.
LibVLC 3 has no thumbnail feature, so a hidden, silent second player jumps there and grabs a frame. That takes
about a fifth of a second, so the previous frame stays up until the new one arrives, and moving quickly along
the bar only fetches the point you stop on. The last 150 frames seen come back instantly, and a resting
pointer (or a wobble of a pixel or two) never changes the frame.

### Resume

Close MorphPlayer, stop, or switch to another file, and it remembers where you were. Open that file again
later and it carries on from there, with a **Resumed at 12:34 · Start over** banner for a few seconds.
Watching to the end forgets the position, so the next time starts from the top.

As in VLC, files under 3 minutes (most songs) always start from the beginning, and positions within 30
seconds of the start or the end aren't kept. Network streams are never resumed. Positions for the last 300
files are kept, in `%LOCALAPPDATA%\MorphPlayer\state.json`.

### Audio and subtitle tracks

The **Audio Track** and **Subtitles** submenus of the right-click menu (and the ♪ and CC buttons) list every
track in the file, with its language where the file names one, for example a director's commentary or
subtitles in several languages. **V** and **B** cycle through them without a menu.

### Subtitle files

MorphPlayer finds subtitle files that belong to the video, including ones whose names don't match it exactly
(LibVLC's own matching, which only finds names containing the video's whole file name, is switched off):

| Subtitle file | Found for |
| --- | --- |
| `Movie.srt`, `Movie.en.srt`, `Movie.eng.forced.srt` | `Movie.mkv`: the same name, or the same name with more after it |
| `The Movie (2020) - German SDH.srt`, `The.Movie.2020.WEB-DL.en.srt` | `The.Movie.2020.1080p.BluRay.x264-GRP.mkv`: the same title and year, from another release |
| `Show Name - 1x02.srt`, `Show.Name.S01E02.HDTV.srt` | `Show.Name.S01E02.720p.mkv`: the same title, season and episode. Another episode's subtitles never match |
| `Subs\Movie\2_English.srt` | `Movie.mkv`, in a `Subs` (or `Sub`, `Subtitles`) folder named after it |
| `Subs\2_English.srt` | the video beside the `Subs` folder, when it's the only video there |

Each one is named in the menu by the language and flags at the end of its file name: `en`, `eng`, `English`
and `Sinhala` all work, in any language Windows knows, plus **SDH** (also `cc`, or `hi` beside a language) and
**Forced**. So `Movie.eng.forced.srt` shows as **English (Forced)** rather than "Track 3". A name with no
language in it shows as its file name.

One is turned on straight away, as in VLC: full subtitles before forced-only ones, then those in your Windows
display language, then the closest match by name. The rest are in the **Subtitles** menu.

Other `.srt`, `.ass`, `.ssa`, `.vtt`, `.sub`, `.idx` or `.smi` files can be added from the subtitle menu or by
dropping them on the video, and are named the same way.

### Audio device and stereo mode

**Audio Device** lists your Windows outputs, such as speakers, headphones or a Bluetooth headset, and
switches between them without stopping playback. **Default** follows whatever Windows is set to.

**Stereo Mode** changes how the sound is laid out:

| Mode | What it does |
| --- | --- |
| **Original** | The channels as recorded (the default) |
| **Stereo** | Two channels; 5.1 and 7.1 sound is mixed down |
| **Mono** | Everything mixed into one channel, heard in both ears |
| **Left / Right Channel Only** | Just one side, in both ears |
| **Reverse Stereo** | Swaps left and right |
| **Headphones** | For 5.1 and 7.1 sound: a binaural mix, so the surround channels still sound placed around you in headphones. Stereo files play as normal |

These are LibVLC's own stereo modes, the same as VLC's **Audio → Stereo Mode**, and switch instantly. The
audio device and stereo mode you choose are remembered.

### Network streams

**Ctrl+N** opens a box for a URL: `http(s)://`, `rtsp://`, `rtmp://`, `udp://@:1234`, `mms://` and
anything else LibVLC accepts. If the clipboard already holds a URL it is filled in for you.

---

## Keyboard shortcuts

| Key | Action | Key | Action |
| --- | --- | --- | --- |
| **Space** | Play / pause | **F** or **F11** | Fullscreen |
| **Esc** | Leave fullscreen | **S** | Stop |
| **← / →** | Seek 10 s | **Ctrl+← / Ctrl+→** | Seek 60 s |
| **↑ / ↓** | Volume ±5% | **M** | Mute |
| **N / P** | Next / previous | **Delete** | Remove selected playlist items |
| **[ / ]** | Slower / faster | **=** | Reset speed to 1× |
| **V** | Next subtitle track | **B** | Next audio track |
| **Ctrl+O** | Open files | **Ctrl+F** | Open folder |
| **Ctrl+N** | Open network stream | **Ctrl+L** | Show / hide playlist |

---

## How it works

- **Playback** — a single LibVLC `MediaPlayer` renders into LibVLCSharp's WPF `VideoView`, which hosts a
  native window that LibVLC draws into with Direct3D.
- **Overlay** — WPF cannot draw on top of a native child window (the *airspace* problem), so
  `VideoView` puts its content in a separate transparent window kept aligned over the video. The control
  bar, messages and drop target all live in that overlay. Its background is 1/255 opaque rather than
  fully transparent, because Windows does not deliver mouse input to fully transparent pixels of a
  layered window.
- **Threading** — LibVLC raises its events on its own threads. Every handler hops back to the UI thread
  before touching the player, since calling LibVLC from inside its own callbacks can deadlock.
- **Position** — the seek bar and time labels are refreshed by a 250 ms timer rather than LibVLC's
  per-frame `TimeChanged` events.

| File | Purpose |
| --- | --- |
| `src/MorphPlayer/MainWindow.xaml` | Layout: video, overlay, control bar, playlist panel |
| `src/MorphPlayer/MainWindow.xaml.cs` | Player, playlist, shortcuts and all UI logic |
| `src/MorphPlayer/PlaylistItem.cs` | One playlist entry (file path or URL) |
| `src/MorphPlayer/UrlDialog.cs` | The **Open Network Stream** prompt |
| `src/MorphPlayer/AppState.cs` | What's remembered between runs: resume positions, audio device, stereo mode |
| `src/MorphPlayer/FrameGrabber.cs` | Grabs single frames with a hidden player, for playlist thumbnails and seek bar previews |
| `src/MorphPlayer/MovieArt.cs` | Finds a poster image saved beside a video |
| `src/MorphPlayer/SubtitleFinder.cs` | Finds a video's subtitle files and names them by language |
| `src/MorphPlayer/App.xaml(.cs)` | Shared styles (icon buttons, sliders, colours) and LibVLC start-up |
| `installer/MorphPlayer.iss` | Inno Setup script: files, shortcuts, Open with and Default apps registration |
| `publish.ps1` | Release build: portable zip, installer, plugin cache, checksums |
| `tools/icon/make_icon.py` | Draws the app icon (`python -I tools/icon/make_icon.py`, needs Pillow) |

---

## Limitations

- **Windows only**, and releases are **x64 only** (they also run on Windows 11 on ARM through emulation).
  WPF and the `VideoView` used here are Windows-specific.
- **Only resume positions, the audio device and the stereo mode are saved.** Volume, speed, repeat,
  shuffle and the playlist reset each run.
- **Opening another file from Explorer starts a second window** instead of adding to the running one.
- A plain `dotnet build` output is large (about 300 MB) because the LibVLC package ships x86, x64 and
  ARM64 copies of every plugin. Release builds keep only x64.
- **Smart App Control blocks it.** On PCs with Smart App Control turned on, Windows refuses to run
  unsigned apps it hasn't seen before, and unlike SmartScreen it offers no **Run anyway**. Until
  MorphPlayer is code-signed, those PCs can't run it, from a release or from source (*"An Application
  Control policy has blocked this file"*).

---

## Legal

MorphPlayer is not affiliated with or endorsed by VideoLAN. VLC and the VLC cone are trademarks of
VideoLAN.

LibVLC and LibVLCSharp are third-party libraries under the
[GNU LGPL 2.1 or later](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html). They are
used unmodified and dynamically linked; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

MorphPlayer itself is released under the [MIT License](LICENSE).
