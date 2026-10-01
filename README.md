# Jazz Hands

A Windows video editor for game trailers, promo videos and devlogs, with a headless engine that
can be driven completely from the command line. Every edit you can make in the editor is also a
command: the `jazz` CLI, a JSON-RPC control server and an MCP server, so an AI assistant such as
Claude Code can build and change a project, look at frames, and export, live in a running editor
or with no window at all.

- Multi-track timeline with compositing, keyframes, effects, transitions, titles and colour tools
- GPU decoding, compositing and NVENC export (Direct3D 11, FFmpeg 8.1)
- Smart cut: trims long recordings without re-encoding, except the frames around each cut
- Every audio stream of a recording (for example OBS's game, mic and chat tracks) as its own lane
- Speech to text, captions and editing by deleting words, all on your own machine
- Projects are readable JSON with a published schema (`Docs/schema`)

## Download

No release has been published yet; for now, [build from source](#build-from-source). Releases
will be on the [Releases page](https://github.com/Sweet-Karmaz-Studios/Jazz-Hands/releases),
each with two downloads:

- `JazzHands-<version>-win-x64.msi` installs for the current user, with no administrator rights,
  and puts `jazz` on your PATH.
- `JazzHands-<version>-portable-win-x64.zip` runs from any folder and leaves the registry alone.

The builds are not code signed, so Windows SmartScreen asks once: **More info**, then **Run anyway**.

## Requirements

- Windows 11, x64
- A Direct3D 11 GPU. An NVIDIA GPU is recommended for hardware decoding and NVENC export; without
  one Jazz Hands decodes and encodes in software.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell.

```powershell
git clone https://github.com/Sweet-Karmaz-Studios/Jazz-Hands.git
cd Jazz-Hands
powershell -ExecutionPolicy Bypass -File tools\get-ffmpeg.ps1
dotnet build JazzHands.slnx -c Release
```

`get-ffmpeg.ps1` downloads the pinned FFmpeg build into `third_party\ffmpeg` and checks its
SHA-256. The editor is then at
`src\JazzHands.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\JazzHands.exe` and the CLI at
`src\JazzHands.Cli\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\jazz.exe`.

To make the installer and the portable zip yourself:

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

They land in `artifacts\release\<version>`. The installer needs the
[WiX Toolset 5](https://wixtoolset.org) command line (`dotnet tool install --global wix`) with
its UI and Util extensions; `-SkipInstaller` builds only the folder and the zip.

## Driving it from the command line

```powershell
jazz new trailer.jazz --fps 60 --size 3840x2160
jazz media add trailer.jazz .\captures\*.mp4
jazz frame trailer.jazz --at 00:00:12.500 --out check.png
jazz export trailer.jazz --preset youtube-4k
```

`jazz --help` lists every command, and `Docs/CLI.md` is the full reference. `jazz mcp` runs the
MCP server; `jazz mcp --attach` connects it to a running editor so changes appear as they are made.

## Licence

Jazz Hands is licensed under the GNU General Public License, version 3 (`LICENSE.txt`), because it
ships the GPL build of FFmpeg. `LICENSES.md` lists every third-party component and its licence.

Copyright (c) Sweet Karmaz Studios.
