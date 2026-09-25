Lays one file end to end until the run is covered and plays it through the playback engine, with
the sound card's clock at zero monitor volume. Each frame is drawn into a panel-sized surface and
waited for, as the preview panel's presenter does; only WPF taking the surface is left out. Counts
the frames that were due and never shown.

```bash
jazz perf playback tests\corpus\hevc10_2160p60_5s.mp4 --minutes 5
```

```
hevc10_2160p60_5s.mp4: 3840x2160 at 60 fps, 61 clips, on NVIDIA GeForce RTX 4090, clock from Speakers (USB DAC)
  300.0 s, 17998 frames due, 17991 presented
  dropped         7 (0.039%)
  late            p50 0.6 ms, p99 4.5 ms after the frame was due
  present         p99 7.51 ms for the blit and GPU wait
  underruns       0
  notices         none
Inside the bar of 0.1%.
```

"Late" is how far into its frame interval each frame went up. Exit code 1 when more than 0.1% of
the frames due were dropped.
