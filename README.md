# MorphPlayer

**MorphPlayer** is a Windows media player written in C# and WPF on top of
[LibVLC](https://www.videolan.org/vlc/libvlc.html), the playback engine behind VLC.

Because the decoding is done by LibVLC, it plays practically anything VLC plays: MKV, MP4, AVI, WebM,
MOV, FLAC, MP3, Opus, network streams and more, with no codec packs to install. With
[yt-dlp](https://github.com/yt-dlp/yt-dlp), which it fetches and keeps up to date itself, it also plays
YouTube and other website links. Around that engine it puts a minimal window, in a choice of themes and
accent colours, with an auto-hiding control bar, a playlist panel, subtitle and audio track switching,
speed control and keyboard shortcuts for everything.

---

## Download

From the [latest release](https://github.com/DaanishSuhood/MorphPlayer/releases/latest), pick **one**:

| File | What it is |
| --- | --- |
| `MorphPlayer-Setup-<version>.exe` | **Installer.** Installs for you alone (or for all users, if you choose), adds Start menu and desktop shortcuts, offers MorphPlayer under **Open with** for media files, and adds an uninstaller in Settings → Apps. No admin rights needed. |
| `MorphPlayer-<version>-portable.zip` | **Portable version.** Unzip it anywhere, such as a USB stick, and run `MorphPlayer\MorphPlayer.exe`. Nothing is installed; to remove it, delete the folder (and `%LOCALAPPDATA%\MorphPlayer`, where it keeps resume positions and yt-dlp). |

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

If MorphPlayer is already running, they open in that window (see [One window](#one-window)); add
`--new-window` to start a second one.

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
| **Right-click** | Menu: open files, folder or stream; the file's audio and subtitle tracks; audio device and stereo mode; website video quality; playlist; fullscreen; theme and accent colour; updates |
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
seconds of the start or the end aren't kept. Positions for the last 300 files are kept, in
`%LOCALAPPDATA%\MorphPlayer\state.json`.

Website videos resume too. Each one is kept under its page's address, so any link to the same video finds the
same place. A link that names a start time, such as YouTube's `&t=58s`, starts there instead the first time it
plays. Live streams, and network streams that don't go through yt-dlp, are never resumed.

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

### Website links (YouTube and others)

Paste a web page's address into the same box, drop a link from a browser onto the window, or pass one on
the command line. MorphPlayer asks [yt-dlp](https://github.com/yt-dlp/yt-dlp) what plays there, then hands
the stream to LibVLC. Any `http(s)://` address that isn't a media file or a stream playlist (`.m3u8`,
`.mpd`, …) is treated as a web page, so it works for
[every site yt-dlp supports](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md), not just
YouTube.

- **Streamed, not downloaded**: yt-dlp only looks up the stream addresses, and LibVLC plays from them as the
  data arrives, so a long film starts as quickly as a short clip, and seeking jumps straight to the new point.
- **Quality**: the button left of the speed button shows what's playing, such as `1080p60`, and appears only for
  website videos. It and **Website Video Quality** in the right-click menu choose **2160p (4K)**, **1440p**,
  **1080p** (the default), **720p**, **480p** or **360p**.

  Each is a limit that applies to every video: a video that doesn't come in that resolution plays at the highest
  it has below it. While a website video plays, the resolutions it doesn't offer are dimmed. Resolution is measured
  by the shorter side, as yt-dlp does, so 4K ultrawide (3840×1608) counts as 2160p and a vertical 1080×1920 video
  as 1080p. 8K isn't offered: YouTube mostly has it only in AV1.

  Within the limit, MorphPlayer takes the highest frame rate, then the codec chosen at the bottom of the same
  menu, falling back to the other when a video lacks it:

  | Codec | Trade-off |
  | --- | --- |
  | **Prefer VP9 (less data)** (default) | About a quarter less data for the same picture, but starts and seeks about half as fast (roughly 6 s to start and 5–13 s per seek on a long video, against 3–4 s and 4–8 s for H.264) |
  | **Prefer H.264 (faster seeking)** | Faster starts and seeks. YouTube offers H.264 only up to 1080p, so 1440p and 4K are VP9 either way |

  GPUs from about 2016 on (Intel 7th generation, NVIDIA GTX 10-series, AMD RX 400 and later) decode VP9 in
  hardware; older PCs decode it on the CPU, where 1080p60 can stutter. AV1 is never picked while a video has
  anything else, even if that means a lower resolution, because LibVLC 3 decodes it on the CPU. A size offered only
  in AV1 is dimmed in the menu like a size the video doesn't have.

  Changing either setting reloads the video from the same point, and both are remembered. YouTube serves its best
  video and audio as separate streams, and LibVLC plays them together, so no ffmpeg is needed. The audio is AAC
  rather than Opus: LibVLC 3 loses the audio's place when seeking in YouTube's WebM files, so for VP9 MorphPlayer
  opens the AAC audio (an MP4 file) as the main stream and adds the video to it.
- **Playlist entries** take the video's title, channel, length and thumbnail.
- **Captions** appear in the **Subtitles** menu once the video starts: every set the uploader added, and the
  auto-generated set in the video's own language, up to 8 in all. The Windows display language comes first, then
  English. None is turned on, as on YouTube. YouTube's machine translations of the auto-generated captions (often
  over 100 languages) are left out.
- **The first time** you open a link, MorphPlayer offers to download yt-dlp and
  [Deno](https://deno.com), the JavaScript runtime yt-dlp needs for YouTube: about 65 MB from their GitHub
  releases, each checked against its published SHA-256. If you say no, the link goes straight to LibVLC,
  which manages a few sites on its own; **Updates → Get yt-dlp for Website Links** fetches them later.
- They live in `%LOCALAPPDATA%\MorphPlayer\tools`, which is writable even when MorphPlayer is installed for
  all users. The uninstaller removes them.
- Addresses that yt-dlp says it doesn't support go to LibVLC instead. Other errors, such as *This video is
  unavailable*, appear in the top-left corner.
- **When a site limits requests** (YouTube's *Sign in to confirm you're not a bot*, or *HTTP Error 429: Too Many
  Requests*), MorphPlayer says so instead of passing on yt-dlp's suggestion to use browser cookies. It doesn't keep
  retrying, which tends to make the limit last longer; wait a while, often up to an hour, and try again.
- **If a website video stops with an error while playing**, MorphPlayer asks yt-dlp about the page once more and
  carries on from the same point, or shows why it can't.

### One window

MorphPlayer keeps to one window. Opening a file from Explorer, **Open with**, or the command line while it is
running adds the file to that window's playlist and plays it, and brings the window forward (out of the
taskbar, if it was minimized). Starting MorphPlayer again with nothing to open just brings the window forward.

Select several files in Explorer and press **Enter**, and Windows starts MorphPlayer once per file. The first
to arrive plays and the rest queue up behind it, in whatever order Windows sent them.

Each Windows account gets its own window, and only your own account can send files to it. To have two
windows anyway, say to compare two videos, start the second with `MorphPlayer.exe --new-window`.

### Themes and colours

**Theme** in the right-click menu changes the look of everything around the picture: the title bar, the
playlist panel, the menus and tooltips, the **Open Network Stream** box, and the video area while there's no
picture in it.

| Theme | Look |
| --- | --- |
| **Match Windows** | **Dark** or **Light**, whichever Windows is set to for apps (Settings → Personalization → Colors), changing when Windows does |
| **Dark** | Charcoal panels on a black video area (the default) |
| **Black** | Pure black throughout, for OLED screens |
| **Midnight** | Deep navy |
| **Forest** | Deep green |
| **Plum** | Deep purple |
| **Light** | Light panels, menus and title bar. The video area stays black, like a cinema screen |

**Accent Colour** sets the colour of the seek and volume bars, the playing item in the playlist, check marks
and highlighted buttons: **Orange** (the default), **Red**, **Pink**, **Purple**, **Blue**, **Teal**,
**Green** or **Gold**; **Windows Accent**, which follows the accent colour chosen in Windows Settings; or
**Custom…**, which opens the Windows colour picker for any colour at all.

Both change at once, with no restart, and are remembered. The control bar and the messages over the picture
stay light on dark in every theme, since they sit on the video. Where an accent would be hard to see against
its background, such as navy on black or gold on the Light theme's panels, it is lightened or darkened just
enough to stand out (a 3:1 contrast ratio); the colour you picked is still what's saved.

On Windows 11 the title bar takes the theme's own colour. Windows 10 only offers a dark or a light title bar,
so it gets whichever suits the theme.

### Updates

The **Updates** submenu of the right-click menu has:

| Item | What it does |
| --- | --- |
| **Check for MorphPlayer Updates** | Asks GitHub for the latest release. If it is newer, **Update to MorphPlayer x.y.z** appears at the top of the menu (see below) |
| **Update yt-dlp** | Runs `yt-dlp --update-to nightly`, as [SnagDL](https://github.com/DaanishSuhood/SnagDL) does: YouTube fixes reach the nightly builds well before stable ones. Shown as **Get yt-dlp for Website Links** until it's downloaded |
| **Check Automatically** | On by default. Once a day at start-up, MorphPlayer asks GitHub for its latest release and updates yt-dlp in the background |

When a newer MorphPlayer is out, a **MorphPlayer x.y.z is available · Update** banner shows in the top-right
corner for 15 seconds at start-up. Its **×** hides it until the next version. MorphPlayer never updates without
being asked. **Update** downloads the new version from the GitHub release, checks it against the release's
`SHA256SUMS.txt`, closes MorphPlayer, installs it and starts MorphPlayer again:

- **Installed:** the new installer runs silently, keeping your install folder and shortcuts. If MorphPlayer is
  installed for all users, Windows asks for admin rights.
- **Portable:** the new zip is unpacked and copied over the MorphPlayer folder once MorphPlayer has closed.
- **Built from source**, or anything goes wrong (Smart App Control, for one, blocks the unsigned installer): the
  release page opens instead, as **Download**.

Each release includes the newest LibVLC, so this is also how LibVLC security fixes reach you.

The daily check sends one request to `api.github.com`, which sees your IP address and the MorphPlayer version.
Nothing else is sent.

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
| **Ctrl+N** | Open network stream or website link | **Ctrl+L** | Show / hide playlist |

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
| `src/MorphPlayer/YtDlp.cs` | Fetches, updates and runs yt-dlp and Deno, and reads the stream behind a web page from yt-dlp's answer |
| `src/MorphPlayer/UpdateChecker.cs` | Asks GitHub for the latest MorphPlayer release |
| `src/MorphPlayer/Updater.cs` | Downloads and installs a new release: the installer run silently, or the portable zip copied over the folder |
| `src/MorphPlayer/Web.cs` | The shared HTTP client, and downloads checked against a published SHA-256 |
| `src/MorphPlayer/AppState.cs` | What's remembered between runs: resume positions, audio device, stereo mode, website video quality, theme and accent colour, update checks |
| `src/MorphPlayer/SingleInstance.cs` | Keeps MorphPlayer to one window: a named mutex to tell whether it's running, a named pipe to pass it files |
| `src/MorphPlayer/Themes.cs` | The themes and accent colours, and the brushes the XAML uses for whichever are chosen |
| `src/MorphPlayer/TitleBarTheme.cs` | Colours the title bar to match the theme, and notices when Windows' own colours change |
| `src/MorphPlayer/ColorPicker.cs` | The Windows colour dialog, for a custom accent colour |
| `src/MorphPlayer/FrameGrabber.cs` | Grabs single frames with a hidden player, for playlist thumbnails and seek bar previews |
| `src/MorphPlayer/MovieArt.cs` | Finds a poster image saved beside a video |
| `src/MorphPlayer/SubtitleFinder.cs` | Finds a video's subtitle files and names them by language |
| `src/MorphPlayer/App.xaml(.cs)` | Shared styles (icon buttons, sliders, menus, tooltips), and start-up: the one-window check, LibVLC, the theme |
| `installer/MorphPlayer.iss` | Inno Setup script: files, shortcuts, Open with and Default apps registration |
| `publish.ps1` | Release build: portable zip, installer, plugin cache, checksums |
| `tools/icon/make_icon.py` | Draws the app icon (`python -I tools/icon/make_icon.py`, needs Pillow) |

---

## Limitations

- **Windows only**, and releases are **x64 only** (they also run on Windows 11 on ARM through emulation).
  WPF and the `VideoView` used here are Windows-specific.
- **Only resume positions, the audio device, the stereo mode, the website video quality, and the theme and
  accent colour are saved.** Volume, speed, repeat, shuffle and the playlist reset each run.
- **A copy run as administrator is on its own.** Windows doesn't let an ordinary copy pass files to one that
  was started with **Run as administrator**, so opening a file then starts a second window.
- **Website links play one video at a time.** A YouTube playlist link plays the video it points at, not the
  whole list. Videos that need you to be signed in, such as age-restricted ones, don't play, because
  MorphPlayer doesn't pass browser cookies to yt-dlp.
- **YouTube sometimes limits heavy use.** After many links in a short time, or on a busy shared network or VPN,
  YouTube may ask yt-dlp to sign in to prove it isn't a bot. Without browser cookies the only fix is to wait.
- **Deno isn't updated by Update yt-dlp.** If YouTube links stop working even after updating yt-dlp, delete
  `%LOCALAPPDATA%\MorphPlayer\tools` and open a link again to fetch both afresh.
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
