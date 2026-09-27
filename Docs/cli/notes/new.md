Creates an empty project with one video track and one audio track, and saves it.

```bash
jazz new trailer.jazz --fps 60 --size 4k
```

```
Created C:\work\trailer.jazz
  3840x2160 at 60 fps, one video track and one audio track.
```

The `.jazz` extension is added if it is left off.

**Frame rates are exact.** `--fps 29.97` is accepted and stored as 30000/1001, because that is
what the person meant. `--fps 23.5` is refused with the ratio to type instead, because storing
2997/100 would drift by a frame every thousand frames against real footage and nobody would find
out until an export went out of sync.

**The sample.** `jazz new demo.jazz --sample` writes the sample project instead: a 20 second
trailer at 1920x1080 and 30 fps made of the editor's own gradients, particles, titles and sound,
the same as the editor's Help, Open the sample project (`project.sample`). It needs no media, so
it opens anywhere; `--fps`, `--size` and `--name` do not apply to it.
