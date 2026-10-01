# Licenses

## Jazz Hands

Copyright (c) Sweet Karmaz Studios.

Jazz Hands is licensed under the **GNU General Public License, version 3** (`LICENSE.txt`). It has
to be: it ships and links the GPL build of FFmpeg, which includes GPL components such as x264 and
x265, and a program that links them is a derivative work under the GPL. Anyone given a copy of
Jazz Hands is entitled to its complete corresponding source under the same licence, and may
change and share it on the same terms.

## FFmpeg

`ffmpeg\` holds FFmpeg 8.1 (the libraries `avcodec`, `avformat`, `avfilter`, `avutil`, `avdevice`,
`swresample`, `swscale`, and `ffmpeg.exe`) as built by BtbN's FFmpeg-Builds, autobuild tag
`autobuild-2026-09-22-13-18`, the "gpl shared" build for win64. That build includes, among others,
x264, x265, SVT-AV1, dav1d, libvpx, libopus, libvorbis, LAME and vid.stab, under the GPL version 3
or later as a whole (`ffmpeg\LICENSE.txt`).

- FFmpeg: <https://ffmpeg.org>; its source for this build: the FFmpeg repository at the commit the
  tag names, and the build scripts at <https://github.com/BtbN/FFmpeg-Builds>, which fetch every
  component's source.
- FFmpeg is a trademark of Fabrice Bellard.

## ACES

The ACES 2.0 output transform, ACEScct and the input transforms in
`src/JazzHands.Render/Color/Aces` and `src/JazzHands.Render/Shaders/Aces.hlsl` are a port of the
Academy Color Encoding System's reference implementation, `aces-core` (Lib.Academy.OutputTransform,
Tonescale, DisplayEncoding, ColorSpaces and Utilities, at commit `069b0bc`) and the output presets
in `aces-output`, Copyright Contributors to the ACES Project, licensed under the **Apache License,
version 2.0**: <https://github.com/aces-aswf/aces-core>, <https://github.com/aces-aswf/aces-output>.
The camera gamut matrices are the ones the manufacturers' ACES input transforms publish. Nothing
from ACES ships as a file; the port is checked against OpenColorIO (BSD-3-Clause), which is used
only by the development test tools and not shipped.
ACES is a trademark of the Academy of Motion Picture Arts and Sciences.

## Machine learning models

No model ships with Jazz Hands. Each is fetched from its project's own release only when the
person asks (`model.download`, or the button in the Transcript panel that says its size), checked
against a pinned SHA-256, and kept in `%LOCALAPPDATA%\JazzHands\models`.

| Model | Licence | Where from |
|---|---|---|
| whisper large-v3-turbo, whisper.cpp's ggml build (speech to text, Phase 39) | MIT (OpenAI's Whisper weights and whisper.cpp's conversion) | <https://huggingface.co/ggerganov/whisper.cpp> |
| Robust Video Matting, MobileNetV3 (Phase 43) | GPL-3.0 | <https://github.com/PeterL1n/RobustVideoMatting> |
| DeepFilterNet 3 (Phase 43) | MIT or Apache 2.0 | <https://github.com/Rikorose/DeepFilterNet> |

## .NET libraries

Each is used as published on nuget.org, unmodified.

| Library | Licence | Where |
|---|---|---|
| .NET runtime, WPF, Windows Forms interop, Microsoft.Extensions.*, System.CommandLine, System.IO.Hashing, Microsoft.Data.Sqlite | MIT | <https://github.com/dotnet> |
| C#/WinRT (WinRT.Runtime) | MIT | <https://github.com/microsoft/CsWinRT> |
| FFmpeg.AutoGen | MIT | <https://github.com/Ruslan-B/FFmpeg.AutoGen> |
| Vortice.Windows (Direct3D 11, Direct3D 9, DXGI, Direct2D, D3DCompiler, Mathematics) | MIT | <https://github.com/amerkoleci/Vortice.Windows> |
| SharpGen.Runtime | MIT | <https://github.com/SharpGenTools/SharpGenTools> |
| NAudio | MIT | <https://github.com/naudio/NAudio> |
| CommunityToolkit.Mvvm | MIT | <https://github.com/CommunityToolkit/dotnet> |
| AvalonDock (Dirkster) | Microsoft Public License (Ms-PL) | <https://github.com/Dirkster99/AvalonDock> |
| Serilog and its sinks | Apache 2.0 | <https://serilog.net> |
| Model Context Protocol C# SDK | Apache 2.0 | <https://github.com/modelcontextprotocol/csharp-sdk> |
| SQLitePCLRaw | Apache 2.0 | <https://github.com/ericsink/SQLitePCL.raw> |
| SQLite (e_sqlite3) | Public domain | <https://sqlite.org> |
| Ulid | MIT | <https://github.com/Cysharp/Ulid> |
| Humanizer | MIT | <https://github.com/Humanizr/Humanizer> |
| ONNX Runtime (Microsoft.ML.OnnxRuntime.DirectML) | MIT | <https://github.com/microsoft/onnxruntime> |
| DirectML redistributable (Microsoft.AI.DirectML), which ONNX Runtime's DirectML provider loads | Microsoft Software License Terms: redistributable in Windows applications built with machine learning frameworks; not open source (see the note below) | <https://www.nuget.org/packages/Microsoft.AI.DirectML> |
| Whisper.net, Whisper.net.Runtime, Whisper.net.Runtime.Vulkan (whisper.cpp and ggml inside) | MIT | <https://github.com/sandrohanea/whisper.net>, <https://github.com/ggml-org/whisper.cpp> |
| JsonSchema.Net, JsonPointer.Net, Json.More.Net | MIT source; the published binaries come with the json-everything EULA | <https://github.com/json-everything/json-everything> |

DirectML.dll is Microsoft's own licence, not an open source one. It permits shipping it inside a
Windows application built with machine learning frameworks. Whether a GPL build of Jazz Hands may
ship it beside the GPL FFmpeg is a question to settle before a release that includes it. The
alternative is the copy Windows itself carries in System32, if ONNX Runtime accepts its version.

## Audio plugins

Jazz Hands hosts CLAP plugins (Phase 46) through the CLAP 1.2.10 headers, MIT licensed
(<https://github.com/free-audio/clap>). The headers were only read to write the C# declarations and
to build test plugins; nothing of CLAP is shipped. Plugins a person
installs keep their own licences and run in a process of their own.

## Fonts and icons

No fonts are shipped: titles use the fonts installed in Windows (Segoe UI by default) or a
project's own `fonts\` folder. The icons in `Assets\` are drawn for Jazz Hands
(`tools\JazzHands.IconGen`) and are under the same licence as the program.
