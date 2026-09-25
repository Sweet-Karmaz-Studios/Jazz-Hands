Builds a project from one file (a track per audio stream, cut into clips with different in
points, gains and fades, the tracks offset so their cuts never line up) and plays it through the
sound card, seeking back to the start each time it reaches the end. Reports gaps.

```bash
jazz perf audio tests\corpus\obs_3audio_5s.mkv --clips 30 --minutes 10
```

```
obs_3audio_5s.mkv: 30 clips on 3 tracks, played to Speakers (USB DAC)
  600.0 s, 28799266 frames, 14 loop(s)
  underruns       0
  starved blocks  0
  clock drift     -26.6 ms against the wall clock
No gaps.
```

An underrun is the device running dry, which is a gap somebody hears. A starved block is one mixed
before its source was decoded. Exit code 1 when there was an underrun.
