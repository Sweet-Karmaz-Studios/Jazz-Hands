Cuts stretches out of one recording and writes them back to back: a Quick Trim from the command
line. It sends the commands the GUI sends (`media.add`, `trim.start`, `trim.set-segments`,
`track.set-mute`) and exports with the planner and exporter the queue uses, so the same trim made
here and in the editor gives the same bytes.

```bash
jazz trim capture.mkv --keep 00:10-00:25,01:00-01:30 --mute-stream Mic --out cut.mp4
```

```
Smart cut to C:\work\cut.mp4 (mp4), 00:00:45.000
  Smart cut: 1 piece(s) around the cuts, 60 frame(s), encoded again with h264_nvenc; the other 2640 frame(s) are the source's own packets.
  The sound is cut to the sample: PCM in its packets, anything else encoded again with its own codec.
  Picture: stream 0, matched with h264_nvenc then libx264. Sound: Game (stream 1), Discord (stream 3).
  Copy   00:00:10.000 to 00:00:24.000 (frames 600 to 1440, 840 frames)
  Encode 00:00:24.000 to 00:00:25.000 (frames 1440 to 1500, 60 frames)
  Copy   00:01:00.000 to 00:01:30.000 (frames 3600 to 5400, 1800 frames)
  Estimate: about 42.4 MB, about 2 s.
Wrote C:\work\cut.mp4: 42.2 MB, 00:00:45.000 in 0.9 s (50.0x real time) with smart (h264_nvenc).
  60 frame(s) encoded again with h264_nvenc, 2640 copied; every frame checked in place.
```

A smart cut is exact. It needs H.264, HEVC or AV1 at a constant frame rate, progressive, and an
encoder that writes parameter sets describing the same picture as the source's (profile, chroma
format, bit depth, size); NVENC is tried first, then x264, x265 or SVT-AV1, and `--encoder libx264`
(or any of the codec's chain) chooses, which keeps a smart cut off a busy GPU. Sound is cut to the
sample: PCM inside its packets, anything else encoded again with its own codec. The file is
checked packet by packet before it is kept. When the source cannot be smart cut, `auto` copies
instead and the plan says why (`--mode smart` refuses with `cannot-smart-cut`).

A copy cuts on keyframes. Each cut moves to the nearest one, the start back or on and the end on
or back, and every move is printed with the frame it landed on; `--json` puts them in `snaps`.
