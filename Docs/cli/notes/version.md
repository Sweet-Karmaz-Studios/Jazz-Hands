Prints the Jazz Hands version, the .NET runtime, the FFmpeg build with its library versions and
load path, and the Direct3D adapter the compositor would use.

```bash
jazz version
```

```
Jazz Hands 0.1.0
.NET         .NET 10.0.11 (x64)
Windows      Microsoft Windows 10.0.26200
FFmpeg       n8.1.3-20260922 [avcodec 62.28.103, avformat 62.12.103, avutil 60.26.103, ...]
             C:\src\Jazz-Hands\third_party\ffmpeg\bin
GPU          NVIDIA GeForce RTX 4090
Direct3D     Level_11_1, hardware video yes
```

With `--json` the same information comes back as a camelCase JSON object, which is what a bug
report or a CI job should capture.
