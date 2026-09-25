Exports a sequence in the foreground, printing progress on stderr. A running editor's queue is
reached with `jazz export enqueue` instead (Phase 25's `--attach`).

```bash
jazz export trailer.jazz --out renders/trailer.mp4 --preset youtube-1080p
```

`jazz trim` takes the same override options.

A Quick Trim sequence exports its kept stretches back to back; any other sequence exports from its
start to its last clip. The plan says in sentences why it copies or encodes, and `--dry-run` is
the way to ask.

```
Encode to C:\clips\boss.mp4 (mp4), 00:01:00.000
  Encoding, as asked.
  Aiming under 8 MB for 60 s: 957 kb/s of picture at 960x540, after 128 kb/s of sound. Smaller than 1920x1080, which needs about 2177 kb/s to look right. The file is checked when it is done and encoded again, smaller, if it is over.
  Picture: h264 960x540 at 30 fps, h264_nvenc then libx264, 956 kb/s.
  Sound: aac 2 channels at 48000 Hz, 128 kb/s.
  Size: under 8 MB, checked when it is done.
  Estimate: about 7.92 MB, about 2 s.
```
