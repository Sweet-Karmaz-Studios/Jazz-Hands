Decodes a file as fast as the machine allows and reports throughput, managed allocation per
frame, and video memory growth. It renders nothing, so a regression
here is unambiguous.

```bash
jazz perf decode tests\corpus\hevc10_2160p60_5s.mp4 --passes 3
```

```
hevc10_2160p60_5s.mp4
  codec      hevc 3840x2160 @ 60
  decoder    hevc (hardware)
  frames     300 x 3 passes in 1.61 s
  throughput 557.4 fps (9.3x realtime)
  allocation 103.5 bytes/frame, 1 pooled shells
  memory     384 MB peak, +40.8 MB growth across passes
  vram       722 MB used, -0.2 MB growth across passes
```

The allocation figure averages the whole run, warm-up included, so it sits a little above the
steady-state 96 bytes per frame of a long run. The allocation test is the one
that holds the line; this number is for spotting a change, not for quoting.
