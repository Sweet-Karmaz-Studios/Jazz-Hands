Seeks around a file and reports how long a frame takes to reach a texture. This is the number that
decides whether scrubbing feels connected to the mouse, so it is reported as percentiles: a median
of 15 ms with a tail at 90 ms feels worse than a flat 25 ms, because the tail is what the hand
feels.

```bash
jazz perf scrub tests\corpus\hevc10_2160p60_5s.mp4
jazz perf scrub tests\corpus\av1_1080p60_5s.mkv --reverse --requests 300
```

```
hevc10_2160p60_5s.mp4
  path         hardware on NVIDIA GeForce RTX 4090
  pattern      random, exact
  requests     200, 200 served
  p50          41.4 ms
  p95          107.1 ms
  worst        128.4 ms
  cache hits   19 %
  decoders     1 opened
```

The four combinations answer different questions and their numbers are not comparable. Random and
exact is the worst case and is dominated by the decode from the enclosing keyframe; drag is what a
person does; nearest is what a shuttle does.
