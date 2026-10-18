# Third-party components

## TagLibSharp 2.3.0

Metadata reader/writer. Bundled in the published `Lune.exe`; an ordinary development build produces a separate `TagLibSharp.dll`. The complete Lune source and build script are included, allowing a rebuild with a replacement version of TagLibSharp by updating `Lune/Lune.csproj` and running `build.ps1`.

- Source: https://github.com/mono/taglib-sharp/tree/TaglibSharp-2.3.0.0
- Package: https://www.nuget.org/packages/TagLibSharp/2.3.0
- License: LGPL-2.1-or-later. See `licenses/TagLibSharp-COPYING.txt`.
- No modifications to TagLibSharp.

## NAudio.Wasapi / NAudio.Core 2.2.1

Audio decoding through Windows Media Foundation, output device enumeration and WASAPI shared-mode playback. Bundled in the published `Lune.exe`.

- Source: https://github.com/naudio/NAudio/tree/v2.2.1
- Package: https://www.nuget.org/packages/NAudio.Wasapi/2.2.1
- License: MIT. See `licenses/NAudio-LICENSE.txt`.
- No modifications to NAudio.

## yt-dlp

Invoked as a separate process for URL information and audio extraction.

- Source and installation: https://github.com/yt-dlp/yt-dlp
- Upstream core: Unlicense. Official bundled executables also contain third-party components; see the upstream license information and `licenses/yt-dlp-LICENSE.txt`.
- Actual version, download URL, and verified checksum: `tools/sources.txt`.
- No modifications to yt-dlp.

## FFmpeg / FFprobe

Invoked as separate processes for audio conversion. Windows essentials build from gyan.dev, linked by the official FFmpeg download page.

- Official project: https://ffmpeg.org/
- Windows build/source details: https://www.gyan.dev/ffmpeg/builds/
- License/build notice: `tools/FFmpeg-LICENSE.txt`. This essentials build uses GPL components.
- Actual downloaded archive and SHA256: `tools/sources.txt`.
- No modifications to FFmpeg or FFprobe.

## Deno

Invoked only by yt-dlp for its YouTube JavaScript challenge support.

- Source: https://github.com/denoland/deno
- License: MIT. See `licenses/Deno-LICENSE.txt` and upstream third-party notices.
- Actual version, download URL, and verified checksum: `tools/sources.txt`.
- No modifications to Deno.

## .NET / Windows

Lune uses the installed Microsoft .NET 10 Windows Desktop Runtime, WPF, and Windows media codecs. Runtime source and notices: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf .

## Test media

Big Buck Bunny, Blender Foundation. YouTube metadata reports “Creative Commons Attribution license (reuse allowed)”.

- Source: https://www.youtube.com/watch?v=aqz-KE-bpKQ
- Project: https://peach.blender.org/
- Test modifications: audio-only conversion to M4A and MP3, edited test metadata, thumbnail attached as cover art.
- Test files are under `test-output/network-data/converted`, outside the user's music library.

Existing music, artists, and album art shown in local screenshots belong to their respective rights holders. They are read from the user's local collection; Lune does not upload them.
