# Third-party notices

MorphPlayer's own source code is under the MIT License (see [LICENSE](LICENSE)). It depends on the
libraries below, which are restored from NuGet at build time and shipped alongside MorphPlayer as
separate, unmodified DLLs. They are **not** covered by MorphPlayer's MIT License.

| Component | Version | Licence | Source |
| --- | --- | --- | --- |
| LibVLC (`VideoLAN.LibVLC.Windows`) | 3.0.24 | LGPL-2.1-or-later | [code.videolan.org/videolan/libvlc-nuget](https://code.videolan.org/videolan/libvlc-nuget), [code.videolan.org/videolan/vlc](https://code.videolan.org/videolan/vlc) |
| LibVLCSharp | 3.10.2 | LGPL-2.1-or-later | [code.videolan.org/videolan/LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) |
| LibVLCSharp.WPF | 3.10.2 | LGPL-2.1-or-later | [code.videolan.org/videolan/LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) |

The LibVLC plugins bundle further open-source libraries (for example FFmpeg's libavcodec) under their
own licences. Copyright notices for those are part of the VLC source tree.

## Your rights under the LGPL

Because these libraries are dynamically linked, you may replace them with your own modified or newer
builds: swap the DLLs in the `libvlc\` folder, or change the package versions in `MorphPlayer.csproj`
and rebuild. The complete source code of each library is available at the links above.

The full text of the GNU Lesser General Public License, version 2.1, is at
<https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html>.

## Trademarks

VLC, VideoLAN and the VLC cone logo are trademarks of VideoLAN. MorphPlayer is an independent project
and is not affiliated with or endorsed by VideoLAN.
