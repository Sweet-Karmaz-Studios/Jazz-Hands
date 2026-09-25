Times a clip's thumbnails and waveform from a cold cache, the way the timeline asks for them: the
visible stretch of a 1500 pixel timeline at visible priority, the whole clip behind it at idle
priority, and the first sound stream's waveform. Each zoom runs on an empty cache of its own in a
temporary folder. Times are from adding the file, so they include hashing and probing.

```bash
jazz perf thumbs tests\corpus\hevc10_2160p30_10min.mp4
jazz perf thumbs tests\corpus\hevc10_2160p30_10min.mp4 --zoom 100 --zoom 200
```

```
hevc10_2160p30_10min.mp4: 600 s, 3840x2160 hevc 30 fps
  import (hash and probe)       217 ms
     2.4 px/s, every 60 s:  10 visible in    574 ms,   10 shown in     574 ms, exact in     574 ms
    40.0 px/s, every 5 s:   8 visible in    487 ms,  120 shown in    3257 ms, exact in    3335 ms
  waveform                     1633 ms
  strip again, from disk         76 ms
  4 workers; times include the import
```

"Shown" is every thumbnail of the strip on screen, a keyframe near its time at least; "exact" is
when the workers have finished making each one the frame its grid asks for.
