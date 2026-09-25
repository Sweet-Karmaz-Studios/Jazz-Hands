## Guides

### Working from Claude Code

The usual loop, all headless:

```bash
jazz new trailer.jazz --fps 60 --size 4k
jazz media add trailer.jazz captures/*.mp4 --json
jazz describe trailer.jazz
jazz apply trailer.jazz edit.json
jazz frame trailer.jazz --at 00:00:12.500 --out check.png
jazz proof trailer.jazz --out proof.mp4
```

`jazz describe` is how to see the edit before and after a change: the project's settings and
media, then the sequence's tracks top to bottom with their clips in order, gaps, transitions,
markers and what is wrong. At `--detail brief` (the default) it keeps to about 2000 tokens,
listing the start and end of a very long track with a line for the middle; `--range 00:10-00:30`
narrows it, `--detail full` adds ids, sources, effects and every problem, and `--json` gives the
records it was written from.

```
project trailer: 3840x2160 at 60 fps, 2 media, 1 sequence(s)

media
  boss-fight: movie, 00:01:22.500, 3840x2160 60 fps, 3 sound streams
  theme: movie, 00:02:10.000, sound

trailer (01M35X3ZBRVSWSBVJVB9C08WFV)
  3840x2160 at 60 fps, 48000 Hz stereo, bt709 (from the project)
  00:00:06:00 long, 2 tracks, 2 clips

V1 (video, order 0)
  00:00:00:00 00:00:02:00 wide
  00:00:02:00 00:00:04:00 wide

A1 (audio, order 1)
  empty

markers
  00:00:04:00 Wishlist now [chapter]
```

`jazz apply` runs a script of steps, each a command as JSON-RPC sends it, in one session, and
saves only when every step worked. A step names what it made with `"as"` and later steps use it:

```json
[
  { "command": "media.add", "args": { "paths": ["${scriptDir}/captures/boss.mp4"] }, "as": "boss" },
  { "command": "clip.add", "args": { "trackId": "01J0000000000000000000TRKV", "at": "0s", "mediaId": "$boss.id", "duration": "6s" } },
  { "command": "title.add", "args": { "text": "Wishlist now", "preset": "lower-third", "at": "00:00:04.000", "duration": "2s" } },
  { "command": "export.enqueue", "args": { "output": "proof.mp4", "preset": "proof" } }
]
```

`$boss.id` is the first id that step changed, `$boss.ids[1]` the second, and `$last` the step
before. `${scriptDir}`, `${projectDir}` and `${env:NAME}` fill in folders and the environment.
Times are written as on the command line. Comments and trailing commas are allowed. Queries can
be steps; their answers are in the `--json` output. An export queued in a script (or by `jazz
export enqueue` in any headless process) runs there and then, before the next step. `--continue`
runs every step and saves what worked; `--dry-run` runs the steps that only change the project
and saves nothing.

`jazz frame` and `frames` draw exactly as the editor's preview does, pixel for pixel, so what is
looked at is what a person sees there; `jazz export still`, `contact-sheet` and `proof` are what an
export writes. `--gpu warp` (or `JAZZ_GPU=warp`) renders on the software
rasterizer, as the tests and CI do, and `--encoder libx264` keeps an encode off the GPU.

### How the generated verbs work

Nothing in the CLI knows what any generated verb is: the verbs, arguments and options are read
from the registry, so a command is typeable the moment it exists. `jazz clip --help` lists the
clip verbs, and `jazz clip split --help` lists what that one takes.

```bash
jazz clip add trailer.jazz <track-id> --at 00:00:04:00 --media <media-id> --dur 6s --name "wide"
```

```
clip.add: 2 item(s) changed, saved C:\work\trailer.jazz
```

`--no-save` runs a command without writing the project back; `--json` prints `{"ok": true,
"version": 3, "changed": [...], "saved": true}` or the error object. Queries print their answer as
indented `name: value` lines, times as clocks, and as JSON with `--json`. A verb that needs no
project (`media probe`, `effect list`, `fonts list`, `presets`) takes `--project` for what a
project adds, such as its fonts folder. A file a command reads is found from the current folder
when it is there, and from the project's folder otherwise.

**`jazz undo` and `jazz redo` do not work headless**, and say so rather than reporting an empty
history. Undo belongs to an open session, and each headless invocation is a new one. They become
useful with `--attach` in Phase 25.

`jazz project new`, `open` and `save` are hand-written rather than generated, because they are
about which file is open rather than about editing one; see `jazz new`.

### Media

`media add` takes files, folders and globs. A folder is read one level deep unless `--recursive`,
and a run of numbered images inside it becomes one item rather than one per frame.

```bash
jazz media add trailer.jazz .\captures\*.mp4 --folder "footage/day one" --tags raw,approved
jazz media add trailer.jazz .\renders --fps 24
```

| Option | Meaning |
|---|---|
| `--folder <path>` | Where they go in the bin, slash separated. Folders exist because something is in them. |
| `--tags <a,b>` | Tags applied to everything imported. |
| `--color <name>` | A colour label: red, orange, yellow, green, blue, purple, grey. |
| `--conform <policy>` | `fit`, `fill`, `stretch` or `native`: how the picture is fitted to a frame of a different shape. |
| `--deinterlace <auto\|on\|off>` | Deinterlace on decode. `auto` follows what the file says it is. |
| `--vfr-conform <auto\|on\|off>` | Remap variable frame timing onto the project grid. `auto` follows the file. |
| `--recursive` | Look inside sub-folders. |
| `--fps <rate>` | The rate an image sequence plays at. Images carry no timing of their own. |

Media is matched by content, not by path, so importing the same file twice is a no-op rather than
two bin entries.

`media probe` reads a file without importing it, which is how to find out what conforming it
would need before committing to it. The conform options say what to assume, and change the
warnings accordingly.

```bash
jazz media probe trailer.jazz .\captures\phone.mp4 --json
jazz media probe trailer.jazz .\captures\old.mov --deinterlace off
```

```
"warnings": [
  { "code": "interlaced", "message": "The picture is interlaced and deinterlacing is off, so it will comb on motion." }
]
```

Warning codes: `variable-frame-rate`, `interlaced`, `hdr`, `no-audio`, `no-streams`, `cannot-read`.
The import dialog in the application shows this same list, from this same query.

The rest of the bin is `media list`, `media get`, `media set`, `media remove`, `media relink` and
`media reprobe`.

### Audio

A movie put on a video track brings its sound with it: each audio stream becomes a clip on an
audio track of its own, named from the stream title (OBS names them Game, Mic, Discord), and all
of them are linked to the picture. A stream with no title goes on A1, A2 and so on. A second
capture dropped on the timeline lands its microphone on the same Mic track as the first.

```bash
jazz clip add trailer.jazz <v1-id> --at 0 --media <capture-id>
jazz clip add trailer.jazz <v1-id> --at 0 --media <capture-id> --audio false
```

`--audio false` puts down the picture alone. On an audio track, `--stream` picks the stream and
defaults to the first audio one.

```bash
jazz audio mute-stream trailer.jazz <clip-id> --stream 2
jazz audio set-gain trailer.jazz <clip-id> --db -6
jazz audio set-pan trailer.jazz <clip-id> --pan -0.25
jazz audio set-fade-in trailer.jazz <clip-id> --dur 0.5s --curve ease-in-out
jazz audio set-channel-map trailer.jazz <clip-id> left
jazz track set-volume trailer.jazz <track-id> --db -3
```

| Verb | Does |
|---|---|
| `audio mute-stream <clip> [--stream n] [--muted false]` | Switches off the linked audio clip playing stream n. The clip given can be the picture or any clip linked to it. |
| `audio set-gain <clip> --db x` | Clip gain, -144 (silence) to +24. 0 clears it. |
| `audio set-pan <clip> --pan x` | -1 hard left to 1 hard right. Mono clips pan at constant power; stereo clips balance. |
| `audio set-fade-in`, `set-fade-out <clip> --dur t [--curve c]` | `linear`, `ease-in-out` (smooth), `ease-in` (slow start), `ease-out` (fast start), `bezier` (S curve). `--dur 0` removes it. |
| `audio set-channel-map <clip> auto\|left\|right\|mono` | Plays one side of a stereo stream, or the sum, as a mono signal. For a microphone recorded on one channel. |
| `audio detach <clip>` | Unlinks the audio from the picture. The audio clips stay linked to each other. |
| `audio replace <clip> <media> [--stream n]` | Plays another media item's audio in the clip, keeping its place, gain, fades and links. |
| `track set-volume <track> --db x`, `track set-pan <track> --pan x` | The track fader and balance, applied after each clip's own. |
| `... --at t` on `audio set-gain`, `audio set-pan`, `track set-volume`, `track set-pan` | Sets a keyframe at t on the sequence (the first turns automation on), or changes the one there. Without `--at`, a value that has keyframes is refused (`param-animated`) rather than flattened; `param clear-keyframes` turns them off. |
| `audio set-master-volume --db x [--at t] [--sequence s]` | The master fader, after every track and before the limiter. |
| `audio set-limiter [--on true\|false] [--ceiling x] [--sequence s]` | The master's true peak limiter: 5 ms lookahead, on at -1 dBTP until told otherwise. The ceiling is -24 to 0 dBTP. |
| `audio meter [--from t] [--to t] [--sequence s]` | Mixes the stretch offline, master and limiter included, and reports peak (dBFS), true peak (dBTP), RMS, momentary, short-term, loudest short-term and integrated loudness (LUFS), the limiter's most reduction, and each track's peak and RMS. Silence is `null`. The meter starts at `--from`, so for the short-term loudness at a moment start 3 s before it. |

Sound effects are ordinary effects on an audio clip or track: `effect add <clip-or-track> <type>`,
then `param set` and `keyframe add` like any other. The catalogue, each at zero latency:

| Type | Does |
|---|---|
| `audio.eq.parametric` | Low cut, low shelf, four peaking bands, high shelf, high cut. |
| `audio.compressor` | Threshold, ratio, attack, release, soft knee, makeup; RMS or peak detection, channels linked. |
| `audio.gate` | Threshold with hysteresis, attack, hold, release, and a range for softening rather than silencing. |
| `audio.de-esser` | A high shelf that dips only while the sibilance band is over its threshold. |
| `audio.limiter` | A sample peak ceiling for one clip or track, instant attack, no lookahead. |
| `audio.reverb` | Room size, damping, pre-delay, width and mix; rings on past the last clip. |
| `audio.delay` | Time, feedback, mix, ping-pong and a high cut on the echoes. |
| `audio.gain` | Gain in dB. |

```bash
jazz effect add trailer.jazz <voice-track> audio.compressor
jazz param set trailer.jazz <effect-id> threshold -24
jazz track set-volume trailer.jazz <music-track> --db -18 --at 12s
jazz track set-volume trailer.jazz <music-track> --db -6 --at 14s
jazz audio set-limiter trailer.jazz --ceiling -2
jazz audio meter trailer.jazz --from 0 --to 30s
```

`timeline describe` shows what is set on each audio clip:

```
Mic (audio, order 3)
  00:00:00:00 00:00:05:00 capture [linked, stream 2, gain -6 dB, fade in 0.5 s]
```

### Picture

Every clip with a picture can be moved, scaled, turned, faded, blended, cropped and masked. Each
verb changes only what it is given, so `set-transform --rotation 5` leaves the position alone.

```bash
jazz clip set-transform trailer.jazz <clip-id> --x 960 --y -540 --scale 0.5 --rotation 3
jazz clip set-opacity trailer.jazz <clip-id> --opacity 0.8
jazz clip set-blend trailer.jazz <clip-id> screen
jazz clip set-crop trailer.jazz <clip-id> --left 10 --right 10
jazz mask add trailer.jazz <clip-id> --shape ellipse --x 480 --y 270 --width 960 --height 540 --feather 40
jazz mask add trailer.jazz <clip-id> --shape bezier --path "M 100 100 C 400 0 800 0 1100 100 L 600 900 Z"
jazz mask set trailer.jazz <mask-id> --invert true
jazz mask add trailer.jazz <effect-id> --shape rectangle --x 0 --y 0 --width 640 --height 1080 --expansion 20
```

| Verb | Does |
|---|---|
| `clip set-transform <clip> [--x] [--y] [--scale] [--scale-x] [--scale-y] [--rotation] [--anchor-x] [--anchor-y]` | Position in sequence pixels from the frame centre, scale where 1 is the fitted size, rotation in degrees clockwise about the anchor, which is in picture pixels from the picture's centre. |
| `clip set-opacity <clip> --opacity x` | 0 invisible to 1 opaque. |
| `clip set-blend <clip> <mode>` | `normal`, `add`, `multiply`, `screen`, `overlay`, `darken`, `lighten`, `difference`, `soft-light`, `hard-light`. Blending happens in linear light. |
| `clip set-crop <clip> [--left] [--top] [--right] [--bottom]` | Percent of each side cut away. Left and right together, or top and bottom, must leave something. |
| `mask add <clip-or-effect> --shape s ...` | `rectangle` and `ellipse` take `--x --y --width --height` in source pixels; `polygon` and `bezier` take `--path`, SVG path data (M, L, H, V, C, Q, Z). `--feather` softens the edge by that many pixels, `--expansion` grows the shape by that many pixels (shrinks it when negative, -1000 to 1000), `--mode` is `add`, `subtract` or `intersect` with the masks before it, `--invert true` keeps the outside. On a picture effect (clip or track) the masks limit where the effect applies; the rest of the picture passes through. Coordinates are still the clip's source pixels. |
| `mask set <mask> ...` | The same options, plus `--enabled on\|off`. |
| `mask remove <mask>` | Removes it. |

Errors: `not-a-picture` for an audio clip, `not-a-mask-owner` for anything but a picture clip or a picture effect, `value-out-of-range`, `crop-out-of-range`,
`invalid-mask` (a shape with no area, or a path that does not parse or close), `mask-not-found`,
`duplicate-id`. All of it is undoable.

`timeline describe` shows what is set:

```
V2 (video, order 1)
  00:00:00:00 00:00:05:00 overlay [at 960,-540 scale 0.5 rotated 3 deg, opacity 80%, blend screen, ellipse mask add]
```

### Playback

The `playback` verbs drive the playhead of a running editor. A headless `jazz` process has no
transport, so they refuse with `no-playback` until `--attach` arrives in Phase 25; they are here
because every command is on every surface. The in and out points are the exception: they are
sequence data, so they work headless given a time, and are undoable like any other edit.

```bash
jazz playback set-in trailer.jazz --at 00:00:04:00
jazz playback set-out trailer.jazz --at 00:00:12:00
jazz playback clear-in-out trailer.jazz
```

| Verb | Does |
|---|---|
| `playback play`, `pause`, `toggle`, `stop` | Stop goes back to where playback last started. Play on the last frame starts from the top. |
| `playback seek <time>` | Moves the playhead; two seeks within 250 ms while stopped are a scrub. |
| `playback step [--frames n]` | Pauses and moves by whole frames; negative steps back. |
| `playback shuttle --rate r` | -32 to 32. Forward rates from 0.25 to 2 keep their pitch; others are silent. Past 2 the picture shows keyframes. |
| `playback loop [on\|off]` | Loops over the in and out points, or the whole sequence. Leave the value out to flip it. |
| `playback go-to <target>` | `start`, `end` (the last frame), `next-edit`, `prev-edit`, `next-marker`, `prev-marker`, `in`, `out`. Fails with `no-target` when there is nowhere to go. |
| `playback set-quality <q>` | `full`, `half`, `quarter` or `auto` (Half while scrubbing or shuttling, Full 150 ms after). |
| `playback set-in`, `set-out [--at t]` | The frame at the out point is inside the range. Default is the playhead. |
| `playback clear-in-out` | Removes both. |
| `playback state` | Where the playhead is, the rate, the quality asked for and in effect, loop, in and out, frames presented and dropped, and under `render` the render pools' counts: targets created, rented and in use, frame textures created, layers cached. Created counts that stay flat while playing mean nothing is allocated per frame. |

In the editor the same commands are on the keys: Space, J K L (holding K steps with J and L),
Left and Right (Shift for ten frames), Up and Down for edits, Home and End, I and O (Shift to go
to them), Ctrl+Shift+X to clear, Ctrl+L to loop, F11 for full screen on the other monitor.

### Selection

The selection is what the editor is pointing at: clips and markers of the active sequence. It is
shared by the timeline, the inspector and every remote client, so a script can select and then
act the way a person clicks and then presses a key. It is not part of the project, is never
saved and is not undone. Headless it lasts as long as the process, which makes it useful in a
`jazz apply` script now and with `--attach` from Phase 25.

```bash
jazz selection set trailer.jazz <clip-id>,<clip-id>
jazz selection set trailer.jazz <marker-id> --mode add
jazz selection get trailer.jazz
jazz clip nudge trailer.jazz <clip-id>,<clip-id> --frames -2
```

| Verb | Does |
|---|---|
| `selection set <ids> [--mode m]` | `replace` (the default), `add`, `remove` or `toggle`. Selecting an id that is not a clip or marker of the active sequence is refused with `not-found`; removing one is not. |
| `selection clear` | Selects nothing. |
| `selection get` | `{"ids": [...], "sequenceId": "..."}`, in the order things were selected; `ids` is left out when nothing is. |
| `clip nudge <ids> [--frames n]` | Moves the clips n frames later (negative earlier), keeping their tracks and their spacing. All or nothing: `before-start`, `would-overlap` or a locked track refuses the lot. |

Ids are taken literally. It is the timeline that widens a click to a clip's linked sound; a
script that wants both names both.

### Editing

The editing toolset (Phase 13). Everything that moves time ripples along every sync-locked track
as well as the clips' own, so sound stays under its picture; a sync-locked clip that would have to
move into something refuses the edit with `sync-lock-blocked` and its name. Locked tracks never
move. A Quick Trim refuses these with `quick-trim`.

```bash
jazz clip ripple-trim trailer.jazz <shot>,<its-sound> --edge end --to 00:00:07:12
jazz clip insert trailer.jazz <track-id> --at 00:00:04:00 --media <media-id> --in 00:01:10:00 --dur 2s
jazz clip freeze-frame trailer.jazz <clip-id> --at 00:00:12:03 --dur 1s
jazz track set-sync-lock trailer.jazz <music-track-id> false
jazz timeline set-magnetic trailer.jazz true
jazz clipboard copy trailer.jazz <clip-id>,<clip-id> > clips.json
jazz clip paste other.jazz --data "$(cat clips.json)" --at 00:00:30:00 --insert
```

| Verb | Does |
|---|---|
| `clip ripple-trim <ids> --edge start\|end --to <t>` | Moves one edge of clips that share it and ripples everything after. A start edge keeps the clip where it begins and takes source off its front. |
| `clip ripple-delete <ids>` | Removes clips and closes the time they took. `clip remove --ripple` is the same for one clip. |
| `clip close-gap <track-id> --at <t>` | Closes the empty space on a track at a time. `not-a-gap` when nothing follows. |
| `clip lift --from <t> --to <t> [--tracks ids]` | Takes a range out and leaves a gap; clips across the ends are cut there. Every unlocked track unless named. |
| `clip extract --from <t> --to <t> [--tracks ids]` | Takes a range out and closes it. |
| `clip insert <track-id> --at <t> ...` | `clip add`'s options; cuts what is there and pushes it on. A movie brings its sound. |
| `clip overwrite <track-id> --at <t> ...` | `clip add`'s options; over what is there, on its sound tracks too. Nothing moves. |
| `clip freeze-frame <id> --at <t> [--dur d] [--id new]` | Holds the frame at a time (two seconds unless told), pushing the rest on. The hold plays no sound and trimming it never changes its frame. |
| `clip storyline-move <ids> --to <t>` | Moves clips on the lowest picture track to the nearest cut, closing up behind them; clips on other tracks that start over them ride along. |
| `clip paste --data <json> --at <t> [--track id] [--insert] [--sequence id]` | Pastes what `clipboard copy` returned: same track numbers (or from `--track`), new ids, links and groups kept, media reused by hash or path or added. |
| `clipboard copy <ids>` | The clips and their media as JSON, full paths, for `clip paste`. Changes nothing. |
| `clip match-frame <id> --at <t>` | `{"clipId", "mediaId", "sequenceId", "sourceTime", "path"}`: which frame of which source the clip shows there. |
| `track set-sync-lock <track-id> true\|false` | Sync lock is on unless turned off; off, ripples on other tracks leave the track alone. |
| `timeline set-magnetic true\|false [--sequence id]` | Keeps the lowest picture track gapless after every edit, and makes `clip move` along it a storyline move. |

Range markers and chapters are `marker add --dur` and `--chapter`; nesting is `clip nest` and
`clip unnest`; selecting is `selection set`. In a script, times may be written as text
(`"90f"`, `"00:00:04.000"`) and are read at the project's rate; `tests/scripts/trailer-edit.json`
is a worked example.

### Quick Trim

A Quick Trim is a sequence over one file laid out at its source times: the kept stretches are
clips, and a cut is a gap. `jazz trim` above is the one-line form.

| Verb | Does |
|---|---|
| `jazz trim start <project> <media-id>` | Makes a Quick Trim sequence for the file, keeping all of it, and shows it. |
| `jazz trim set-segments <project> <ranges>` | Keeps exactly these stretches, written as for `--keep`. |
| `jazz trim add-segment <project> --in <t> --out <t>` | Keeps one more stretch, joining any it touches. |
| `jazz trim remove-range <project> --in <t> --out <t>` | Cuts a range out of whatever it crosses. |

### Export

| Verb | Does |
|---|---|
| `jazz export plan <project> <output>` | The plan without running it: mode, stretches, encoders, snaps, reasons, estimate. Takes the override options `jazz export` takes. |
| `jazz export enqueue <project> <output>` | Plans and queues an export in a running editor; the job id comes back as the changed id. Takes the override options, and `--priority low\|normal\|high`, `--open-folder` and `--run <script>` (given the file's path when it is done). A headless process has no queue and says to use `jazz export`. |
| `jazz export batch <project> <folder>` | Queues an export for each range marker (`--markers`, optionally `--name-contains`), named `01 Intro.mp4` and on, or each of `--ranges 00:10-00:20,01:00-01:30`, named for the sequence. All are planned first; one that cannot be is refused before any is queued. |
| `jazz export still <project> <file.png> --at <t>` | Writes the frame at a time as a PNG, straight away, in any process. `--size` fits it. |
| `jazz export contact-sheet <project> <file.png>` | Frames from the middle of even steps through what an export plays, tiled with their times: `--columns 4 --rows 4 --width 1920`, `--start`, `--end`. Straight away, in any process. |
| `jazz export pause <project> [job-id]` | Holds a job, or every one; a running job stops and starts again from the top when resumed. |
| `jazz export resume <project> [job-id]` | Lets a paused job, or every one, run again. |
| `jazz export set-priority <project> <job-id> low\|normal\|high` | Moves a job that has not started. |
| `jazz export log <project> <job-id>` | What a job has done, a line a step. |
| `jazz export cancel <project> <job-id>` | Stops a queued, paused or running export; the partial file is deleted. |
| `jazz export clear <project>` | Takes finished jobs off the queue. |
| `jazz export list <project>` | The queue with progress, priority and whether a job is on the GPU. Empty in a headless process. |

### Presets (Phase 22)

These need no project.

| Verb | Does |
|---|---|
| `jazz presets list [--category youtube\|discord\|device\|archive\|web\|image\|audio\|other]` | Every export preset with a line on what it writes, built in and your own. |
| `jazz presets get <name>` | A preset in full, as its file has it. |
| `jazz presets save <name> --from <preset> [overrides]` | Saves a preset of your own to `%APPDATA%\JazzHands\export-presets`: a copy of another with the same override options `jazz export` takes, and `--label`, `--description`. Or `--json <file or text>` for a whole preset. A preset with a built-in's name replaces it. |
| `jazz presets delete <name>` | Deletes one of yours; a built-in one of the same name comes back. |

### Cache and proxies

Thumbnails, waveforms, probes and keyframe indexes live in one cache per user, keyed by each
file's content, so a moved file keeps them. Proxies live beside them. Both follow the file, not
the project: another project with the same footage finds them.

| Verb | Does |
|---|---|
| `jazz proxy generate <project> --media <id>` | Makes a proxy of one file: half size H.264 with every frame a keyframe. `--all` for every movie, `--auto` for the ones worth it (4K, AV1, 10-bit HEVC), `--scale 0.25` for quarter size, `--preset proxy-dnxhr-lb` for DNxHR. Headless it is made before the command returns; in the editor it goes on the export queue and the job id comes back. |
| `jazz proxy list <project>` | Each movie's proxy: none, queued, running (with progress), ready (size, path) or failed (why), and whether one is suggested. |
| `jazz proxy set-enabled <project> true` | Plays proxies instead of their sources in the running editor. Export never uses them. |
| `jazz proxy remove <project> --media <id>` | Deletes a proxy (`--all` for the project's). |
| `jazz cache stats <project>` | Where the cache is, its size limit, and what it holds. |
| `jazz cache clear <project>` | Empties what is made again on demand: thumbnails, waveforms, probes, keyframe indexes. `--thumbs`, `--waveforms`, `--probes`, `--keyframes` for one part, `--proxies` for proxies, `--all` for everything, `--media <id>` for one file. |
| `jazz cache configure <project> --cap-gb 50` | Sets the size limit for thumbnails and waveforms (0 for none), evicting at once if over. `--location <folder>` moves the cache from the next start; nothing is copied. |

A true or false option can be given on its own: `--auto` is `--auto true`. Put it after the
positional arguments, because a word following it is read as its value.

### Effects, parameters and keyframes

Every parameter belongs to something with an id, a clip, a track, an effect or a mask, and is
named the same way everywhere. A clip's own are `transform.position`, `transform.scale`,
`transform.rotation`, `transform.anchor`, `opacity`, `crop.left` (`.top`, `.right`, `.bottom`),
and for sound `volume` and `pan`; a generator clip also has its own (a solid's `color`). An
effect's are whatever `jazz effect list` shows for its type. Values are text: `12`, `"100, 50"`,
`"#FF8800"` (sRGB), `both`, `true`.

```bash
jazz effect list trailer.jazz --kind video
jazz effect add trailer.jazz <clip-id> video.blur.gaussian --id <effect-id>
jazz keyframe add trailer.jazz <effect-id> radius --at 00:00:04.000 --value 0
jazz keyframe add trailer.jazz <effect-id> radius --at 00:00:06.000 --value 40 --interp ease-in-out
jazz param set trailer.jazz <clip-id> transform.scale 0.5
jazz keyframe add trailer.jazz <clip-id> transform.position --at 1s --local --value "-300, 0"
jazz param list trailer.jazz <clip-id>
```

| Verb | Does |
|---|---|
| `effect list [--kind video\|audio\|generator] [--search s]` | Every type with its parameters, types, defaults, limits and choices. |
| `effect add <owner> <type> [--index n] [--id]` | Adds an effect to a clip or a track. Picture effects go on video and adjustment clips and tracks, sound effects on audio ones. |
| `effect remove <effect>`, `effect move <effect> --index n` | Takes it off; moves it in its chain (effects run first to last). |
| `effect set-enabled <effect> false` | Bypasses it without removing it. |
| `effect set-param <effect> <param> <value> [--at]` | The same as `param set`, for an effect. |
| `effect reset <effect> [--param p]` | Back to the defaults, keyframes and all. |
| `effect get <effect>` | Its type, place and every parameter with its keyframes. |
| `effect copy <ids>`, `effect paste <owner> --data <json> [--index]` | Copies effects (an owner's id copies its chain) as JSON and pastes them with new ids. |
| `effect save-preset <ids> --name n`, `effect apply-preset <owner> <preset>`, `effect remove-preset <preset>`, `effect list-presets` | Effect chains saved in the project, applied by id or name. |
| `effect export-preset <preset>`, `effect import-preset --data <json> [--name]` | A preset as JSON, for another project. |
| `param set <owner> <param> <value> [--at t] [--local]` | A constant; on a parameter with keyframes, `--at` sets the keyframe there, and without it the command is refused rather than dropping the animation. The default value stores nothing. |
| `param clear-keyframes <owner> <param> [--at t]` | Turns animation off, keeping the value at `--at` (the first keyframe's without it). |
| `param list <owner>`, `param get <owner> <param> [--at t]` | Parameters with their values and keyframes, times on the sequence. |
| `keyframe add <owner> <param> --at t [--value v] [--interp i]` | Adds a keyframe, or changes the one at that time. Without `--value` it takes what the parameter is worth there. A new keyframe takes the shape of the one before it. |
| `keyframe remove`, `keyframe move --to t`, `keyframe set-value --value v`, `keyframe set-interp --interp i` | By `--at`; a keyframe within half a frame of it is found. Removing the last leaves its value as a constant. |
| `keyframe set-handles <owner> <param> --at t [--in "x, y"] [--out "x, y"]` | Bezier handles, time and value from 0 to 1 across the segment. |

Times are on the sequence; `--local` reads them from the clip's start. Keyframes are stored
relative to the clip, so moving it carries its animation. A time outside the clip is refused
with a hint about `--local`. Interpolation is `hold`, `linear`, `bezier`, `ease-in`, `ease-out`
or `ease-in-out`; switches, choices, text and paths always hold.

Errors: `target-not-found`, `unknown-param` (listing what there is), `unknown-effect` (with the
nearest name), `wrong-effect-kind`, `not-an-effect` (a generator), `not-an-effect-owner`,
`effect-not-found`, `index-out-of-range`, `invalid-value`, `value-out-of-range`,
`param-animated`, `not-animated`, `not-animatable`, `not-interpolable`, `keyframe-not-found`
(listing where they are), `keyframe-exists`, `handle-out-of-range`, `time-out-of-range`,
`preset-not-found`, `duplicate-name`, `invalid-data`, `no-effects`, `generator-parameters`.

#### The library (Phase 16)

| Folder | Types |
|---|---|
| Blur | `video.blur.gaussian`, `video.blur.directional`, `video.blur.radial`, `video.sharpen` |
| Distort | `video.lens-distortion`, `video.mirror`, `video.tile` |
| Keying | `video.key.chroma` (tolerance, softness, spill, choke, feather, `view matte`), `video.key.luma` |
| Stylize | `video.drop-shadow`, `video.find-edges`, `video.flicker`, `video.glow`, `video.invert`, `video.noise`, `video.pixelate`, `video.posterize`, `video.vignette` |
| Transform | `video.transform`, `video.crop` (feathered), `video.ken-burns` |
| Generators | `gen.solid`, `gen.gradient`, `gen.noise`, `gen.checkerboard`, `gen.countdown`, `gen.timecode` |
| Shapes | `gen.shape.rectangle`, `gen.shape.ellipse`, `gen.shape.line`, `gen.shape.arrow` |

A generator is a clip, not an effect: `clip add` makes one, and its settings are the clip's own
parameters, set and keyframed on the clip id.

```bash
jazz clip add trailer.jazz <track-id> --at 00:00:10.000 --generator gen.countdown --dur 5s
jazz param set trailer.jazz <clip-id> from 10
jazz clip add trailer.jazz <track-id> --at 0 --generator gen.shape.arrow --id <arrow-id>
jazz keyframe add trailer.jazz <arrow-id> end --at 0 --local --value "-400, 0"
jazz keyframe add trailer.jazz <arrow-id> end --at 1s --local --value "400, 0" --interp ease-out
```

Grain, noise and flicker are seeded from the clip or effect id, so the same frame always looks the
same in the preview, in `jazz frame` and in an export.

### Colour, HDR and scopes (Phase 17)

The colour effects are ordinary effects (`effect add <clip> color.wheels`), with parameters set
by `param set` like any other. Curves are text points, x,y from 0 to 1; a LUT is a `.cube` path,
full or relative to the project's folder.

```bash
jazz effect add trailer.jazz <clip-id> color.basic --id <fx>
jazz param set trailer.jazz <fx> temperature 25
jazz effect add trailer.jazz <clip-id> color.wheels --id <wheels>
jazz param set trailer.jazz <wheels> gain "0.06, 0.015, -0.05, 0"
jazz effect add trailer.jazz <clip-id> color.curves --id <curves>
jazz param set trailer.jazz <curves> master "0,0 0.25,0.2 0.75,0.82 1,1"
jazz effect apply-preset trailer.jazz <clip-id> "Cinematic Teal/Orange"
jazz clip set-tone-map trailer.jazz <clip-id> --operator hable --peak 4000
jazz color sample trailer.jazz --at 00:00:12.000 --x 120 --y -40 --before <white-balance-id>
jazz scopes measure trailer.jazz --at 00:00:12.000
```

| Type | Does |
|---|---|
| `color.basic` | Exposure (stops), contrast about 18% grey, temperature and tint (Bradford), saturation, vibrance. Linear light. |
| `color.wheels` | Lift, gamma, gain and offset, each `"r, g, b, master"`; saturation, contrast and pivot. Perceptual values. |
| `color.curves` | `master`, `red`, `green`, `blue` tone curves; `hue-vs-sat`, `hue-vs-hue`, `sat-vs-sat` (flat at 0.5 for no change). |
| `color.hsl` | A hue, saturation and luma qualifier with softness and invert, then hue shift, saturation and lightness inside it; `view matte`. |
| `color.lut` | A 3D `.cube` LUT: `file`, `intensity`, `domain` (`srgb`, `linear`, `logc`), `interpolation` (`trilinear`, `tetrahedral`). |
| `color.white-balance` | `neutral`, a colour that should be grey (pick it with `color sample --before`), and `amount`. |

| Verb | Does |
|---|---|
| `clip set-tone-map <clip> [--operator] [--peak] [--desaturate] [--reset]` | How an HDR clip comes down to SDR: `bt2390` (default), `hable`, `mobius` or `clip`; the source's peak in nits over the file's MaxCLL or mastering level; how much compressed highlights desaturate. `--reset` follows the project again. |
| `project set-tone-map [--operator] [--desaturate]` | The default for every clip that does not say. |
| `color sample --at t [--x] [--y] [--before <effect>] [--size]` | The colour at a point (sequence pixels from the centre), averaged over a square, in linear light and as hex. `--before` reads the picture with that effect and those after it off. |
| `scopes measure --at t` | Histograms (red, green, blue, luma), clipping at each end, the 1% and 99% luma and the mean, on the delivered signal. |
| `effect list-presets` | Now also lists the built-in looks (`builtIn: true`): Cinematic Teal/Orange, Bleach Bypass, Game Capture Punch. `apply-preset` takes them by name; they cannot be removed. |

Errors: `value-out-of-range` (a peak outside 100 to 10000 nits, desaturation outside 0 to 1, a
sample square outside 1 to 64), `point-outside-frame`, `time-out-of-range`, `no-renderer`,
`built-in-preset`, `invalid-value` (`--reset` with anything else).

HDR sources (PQ and HLG) are tone mapped on the way in, from the file's peak (MaxCLL, else the
mastering display's, else 1000 nits) to SDR white at 203 nits, and BT.2020 is brought into BT.709.
Exports are BT.709 SDR and tagged so.

### Transitions (Phase 18)

A transition sits on a cut: two clips on one track, the first ending where the second starts.
Inside it the outgoing clip plays on past its end and the incoming one starts early, so each
needs source beyond the cut. Centred, half is before the cut; `end-of-left` is all before it,
`start-of-right` all after. The type and duration default to the project's (a one second
crossfade, and an equal power crossfade for sound). A picture transition also crossfades the
clips' linked sound at the same cut unless `--audio false`, and changing or removing one changes
or removes the other unless `--linked false`.

```bash
jazz transition add trailer.jazz <outgoing-clip> <incoming-clip> --type transition.wipe.iris --dur 20f
jazz transition set-param trailer.jazz <transition-id> centre "300, -100"
jazz transition set trailer.jazz <transition-id> --dur 1s --alignment end-of-left
jazz transition add-all trailer.jazz --track <track-id> --type transition.crossfade --dur 12f
jazz transition set-default trailer.jazz transition.push --dur 15f
jazz transition apply-default trailer.jazz --at-cut 00:00:12.000 --kind video
jazz transition list trailer.jazz
jazz transition remove trailer.jazz <transition-id>
```

| Verb | Does |
|---|---|
| `transition add <left> <right> [--type] [--dur] [--alignment] [--handles] [--audio] [--id]` | Puts a transition on a cut, fitted to what the clips have room for. `--handles refuse` (the default) refuses when a clip is short of source past the cut and says by how much; `trim` trims the clips back from the cut until they have it, rippling what follows; `hold` adds it anyway and the missing frames hold. |
| `transition set <id> [--type] [--dur] [--alignment] [--linked]` | Changes the type (keeping the parameters the two share), the duration or the alignment. Consecutive duration changes merge into one undo step, as a drag's do. |
| `transition set-param <id> <param> <value>` | One parameter; the same as `param set` with the transition's id. |
| `transition remove <id> [--linked]` | Takes it off, and the one at the linked cut. |
| `transition apply-default --at-cut t [--track] [--kind video\|audio\|both] [--handles]` | The default transition on the cut nearest `t` (within a second), on every track of that kind with a cut there, as Ctrl+D does at the playhead. `--handles hold` by default. |
| `transition add-all --track <id> [--type] [--dur] [--alignment] [--handles] [--audio]` | One on every bare cut of a track, as one undo step. With `--handles refuse` a short cut refuses the lot and names every one. |
| `transition set-default [type] [--dur]` | The picture or sound type the shortcuts add, and their duration. |
| `transition list [--track] [--sequence]` | Each transition with its cut, where it plays, whether it was fitted, how short of source each clip is, and its parameters. |

| Type | Parameters, besides `easing` (`linear`, `ease-in`, `ease-out`, `ease-in-out`, `custom`) and `curve` (the custom bezier, `x1, y1, x2, y2`) |
|---|---|
| `transition.crossfade` | `style`: `cross` (even to the eye) or `film` (in linear light). |
| `transition.dip` | `colour`, `hold` (the share spent on the colour). |
| `transition.wipe.linear` | `angle`, `softness`, `border`, `border-colour`. |
| `transition.wipe.clock` | `start-angle`, `direction`, `softness`, `border`, `border-colour`. |
| `transition.wipe.radial` | `corner`, `direction`, `softness`, `border`, `border-colour`. |
| `transition.wipe.iris` | `shape` (`circle`, `diamond`, `box`), `centre`, `direction` (`open`, `close`), `softness`, `border`, `border-colour`. |
| `transition.push` | `direction`: the way the pictures move. |
| `transition.slide` | `direction`, `mode` (`in`, `out`). |
| `transition.zoom` | `amount`, `centre`. |
| `transition.blur-dissolve` | `radius`. |
| `transition.glitch` | `intensity`, `block-size`, `seed`. |
| `transition.audio.equal-power` | Sound only: sine and cosine gains, level held through the middle. |
| `transition.audio.linear` | Sound only: straight gains, for two takes of one sound. |

Your own: a `name.hlsl` in `%APPDATA%\JazzHands\transitions` that includes `Transition.hlsli`
and defines `PsMain`, with an optional `name.json` manifest (`id`, `name`, `category`,
`description`, `entry`, `params`), is the type `transition.user.name`. Its parameters reach the
shader in order through `Values` and `More`, a colour through `Tint`. A saved edit is compiled
before the next frame; one that does not compile cuts in the middle and says why in the log.

Errors: `not-adjacent`, `transition-exists`, `transition-not-found`, `wrong-transition-kind`,
`transition-wrong-track`, `insufficient-handles`, `no-room`, `no-cut`, `invalid-duration`,
`not-a-transition`, `not-an-effect` (a transition given to `effect add`), `track-locked`.
Validation warns `insufficient-handles` and `transition-longer-than-clip`, and `timeline
describe` says where each transition plays and what is wrong with it.

### Titles and fonts (Phase 19)

A title is a `gen.title` generator clip. `title add` makes one from a preset (its look, place,
length and animations, written for 1080 lines and scaled to the sequence), and any option changes
what it names. Without `--track` it goes on the highest video track free for its length, or on a
new track on top. Every option is a parameter of `gen.title`, which `title set-style`,
`param set` and keyframes reach later; `jazz effect list --search gen.title` lists them all.

The text is markup: `[b]`, `[i]`, `[u]`, `[color=#FFCC00]`, `[size=48]` (sequence pixels) and
`[font=Bahnschrift]`, each closed by `[/b]` and so on; `\n` is a new line, `\[` a bracket.

```bash
jazz title add trailer.jazz --at 00:00:04.000 --preset lower-third --text "Alex Rivera\n[size=34]Lead designer[/size]"
jazz title add trailer.jazz --at 20s --text "Wishlist [color=#FFD24D]now[/color]" --preset end-card --dur 4s
jazz title add trailer.jazz --at 1s --text "BIG MOMENT" --preset caption-bold --stroke "8 #000000" --anim-in scale
jazz title set-text trailer.jazz <clip> "Press [E] to start" --plain
jazz title set-style trailer.jazz <clip> --font Bahnschrift --weight black --align left --position "-816, 400"
jazz title set-animation trailer.jazz <clip> --in typewriter --in-dur 1.2s --out fade
jazz title measure trailer.jazz <clip>
jazz title list-presets trailer.jazz
jazz fonts list trailer.jazz --search bahn
```

| Verb | Does |
|---|---|
| `title add --at t [--text] [--preset] [--dur] [--track] [--font] [--size] [--color] [--align] [--box] [--shadow] [--stroke] [--anim-in] [--anim-out] [--name] [--id] [--sequence]` | A title from a preset (`title-card` when left out). `--stroke` is a width, then optionally a colour: `4` or `4 #000000`. `--align` takes `centre` or `center`. The clip is named after the first line of its text unless `--name` says otherwise. |
| `title set-text <clip> <text> [--plain]` | What it says. `--plain` takes the text exactly as typed, a bracket being a bracket. A title still named after its text is renamed with it. Consecutive changes to one title merge into one undo step. |
| `title set-style <clip> [--preset] [--font] [--weight] [--italic] [--size] [--color] [--align] [--valign] [--position] [--width] [--line-spacing] [--tracking] [--stroke] [--box] [--box-padding] [--box-radius] [--shadow] [--shadow-offset] [--shadow-blur]` | Its look. A preset restyles it first (look and place, not text or animation), the options over that. A keyframed parameter is refused, as `param set` refuses it. |
| `title set-animation <clip> [--in] [--in-dur] [--out] [--out-dur]` | How it comes in and goes out: `none`, `fade`, `slide-left`, `slide-right`, `slide-up`, `slide-down`, `scale`, `typewriter`, `word-reveal`, `blur` or `wipe`. The animation becomes keyframes on the title's own channels (`fade`, `offset`, `zoom`, `blur`, `reveal`), replacing what was there; what is left out stays. |
| `title measure <clip> [--at]` | Where its text sits on the frame, laid out as it is drawn: the block, the lines, the box, the box's corners after every transform, and whether it stays inside title safe (the middle 90%). At the clip's middle when `--at` is left out. |
| `title list-presets` | Every preset, built in or your own, with what it sets. |
| `fonts list [--search]` | The families a title can use: the project's `fonts` folder first, then those installed. |

Presets: `title-card`, `lower-third`, `end-card`, `caption`, `caption-bold` and `subtitle`. Your
own are `.json` files in `%APPDATA%\JazzHands\titles` in the same format (`name`, `label`,
`description`, `text`, `duration`, `animation { in, inDuration, out, outDuration }`, `params`
as command-line text for a 1080 line frame); one with a built-in's name replaces it.

Fonts: a `fonts` folder beside the project file (`.ttf`, `.otf`, `.ttc`, `.otc`) is read before
the installed fonts, so a project takes its look to another machine. A family that is in neither
is drawn in Segoe UI, and `jazz validate` and `timeline describe` warn `missing-font` with the
clip.

Errors: `unknown-preset`, `not-a-title`, `param-animated`, `invalid-value`, `would-overlap`,
`wrong-track-kind`, `empty-clip`, `empty-title` (measuring a title with no text),
`time-out-of-range`, `track-locked`. `timeline describe --detail full` shows each title's text,
font, size and animations.

### Subtitles and chapters (Phase 21)

A subtitle track holds cues: text in title markup (`[i]`, `[b]`, `[color=#FFCC00]`, `\n`), a time
and a place (bottom unless said). Cues may overlap; overlapping ones stack. The track has a style,
sizes as fractions of the frame's height, and a language. The preview draws subtitle tracks; an
export carries them as `--subtitles` says.

```bash
jazz subtitle read trailer.jazz captions.srt
jazz subtitle import trailer.jazz captions.srt --language eng
jazz subtitle import trailer.jazz --media <capture-id> --stream 3
jazz subtitle add trailer.jazz <track-id> --at 4s --dur 2s --text "[i]Wishlist now[/i]" --align top
jazz subtitle set-style trailer.jazz <track-id> --font Bahnschrift --size 0.05 --box "#000000A0"
jazz subtitle split-long trailer.jazz <track-id> --max-chars 42 --max-lines 2
jazz subtitle export trailer.jazz <track-id> --out captions.vtt
jazz chapter add trailer.jazz "Boss fight" --at 01:12
jazz chapter import trailer.jazz <capture-id> --at 0
```

| Verb | Does |
|---|---|
| `subtitle read [file] [--media m] [--stream n]` | What a file or embedded stream holds: its cues, style, language, and warnings for what will not come across (ASS tags kept but not drawn, WebVTT regions). Changes nothing. |
| `subtitle import [file] [--media m] [--stream n] [--track t] [--language l] [--offset t]` | SubRip, WebVTT or ASS, or a text stream inside a Matroska or MP4 file in the project. Without `--track`, a new subtitle track on top with the file's style and the stream's language. Cues with no length are left out. |
| `subtitle add <track> --at --dur --text [--align] [--id]` | One cue. `--align` is `bottom`, `top`, `middle`, `bottom-left`, `top-right` and so on. |
| `subtitle set-text <cue> --text` | What it says. Typing merges into one undo step. |
| `subtitle set-time <cue> [--at] [--dur \| --end]` | When it shows. |
| `subtitle set-align <cue> <align>` | Where it sits. |
| `subtitle split-long <track> [--max-chars] [--max-lines]` | Wraps cues at word breaks and cuts ones too long into several, at a sentence end near the middle when there is one, sharing the time by length. Formatting stays on its words. |
| `subtitle shift <track> --by t [--from t]` | Moves cues later, or earlier with a negative time. |
| `subtitle replace <track> --find --with [--match-case]` | Find and replace in the text as it reads; a match half in italics is found. |
| `subtitle set-style <track> [--font] [--weight] [--italic] [--size] [--color] [--outline] [--outline-width] [--box] [--shadow] [--margin] [--max-lines] [--max-chars]` | The track's look. Sizes are fractions of the frame's height (0.045 is 49 px at 1080). |
| `subtitle list <track>` | The cues, with their text as markup and as it reads. |
| `subtitle export <track> [--format srt\|vtt\|ass] [--out file]` | The track as a file (UTF-8), or its text. The format comes from `--format`, the extension, or SubRip. |
| `track set-language <track> <code>` | ISO 639-2, such as `eng`; `und` clears it. Also for audio tracks. |
| `chapter add <name> --at t` | A chapter mark: a marker with its chapter flag, which runs to the next chapter or the end. |
| `chapter list` | The chapters with where each starts and ends. |
| `chapter import <media> [--at t]` | A file's own chapters as chapter marks, its start at `--at`. |
| `chapter from-markers` | Every marker on the sequence becomes a chapter. |

Errors: `not-subtitles`, `not-a-cue`, `no-subtitles`, `stream-not-found`, `cannot-read`,
`nothing-to-import`, `no-chapters`, `invalid-value`, `value-out-of-range`, `time-out-of-range`,
`invalid-duration`, `track-locked`. Validation warns `not-a-cue` (a clip on a subtitle track that
says nothing) and `cue-off-subtitle-track`.

### Keys

The editor's editing keys are bindings to these same commands, kept in a keymap. The defaults:

| Keys | Sends |
|---|---|
| Delete | `clip.remove` for each selected clip, as one undo step |
| Shift+Delete | `clip.ripple-delete` of the selection, closing the gap on every sync-locked track |
| Q, W | `clip.ripple-trim` of the clips under the playhead, start or end edge, to the playhead |
| Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z) | `undo`, `redo` |
| Ctrl+Alt+D | `clip.duplicate` for each selected clip |
| Ctrl+D, Ctrl+Shift+D, Shift+D | `transition.apply-default` on the cut nearest the playhead: picture, sound, or both |
| `,` `.` (Shift for ten) | `clip.nudge` the selection by a frame |
| I, O | `playback.set-in`, `playback.set-out` at the playhead |
| Ctrl+K | `clip.split` at the playhead: the selected clips under it, or every clip under it when none is selected there |
| Ctrl+A | `selection.set` to every clip |
| Enter, Backspace | `trim.add-segment`, `trim.remove-range` between the in and out points, in a Quick Trim |

Some keys are the editor's own rather than commands, bound to `ui.` actions: V C B N Y U R H pick
the select, razor, ripple, roll, slip, slide, rate stretch and hand tools (`ui.tool.select` and
so on), S toggles snapping (`ui.snap`), F is match frame (`ui.match-frame`), and Ctrl+C, Ctrl+X,
Ctrl+V and Ctrl+Shift+V copy, cut, paste over and paste pushing on (`ui.copy`, `ui.cut`,
`ui.paste`, `ui.paste-insert`) through the Windows clipboard, which they fill with
`clipboard.copy` and empty with `clip.paste`.

A `keymap.json` in `%APPDATA%\JazzHands` changes them a key at a time. Each binding names a
command and its arguments as JSON-RPC would send them, with `$selection`, `$playhead`, `$in`, `$out`,
`$clipsAtPlayhead` and `$allClips` filled in when the key is pressed; a binding with an empty
command frees the key. Bindings that do not parse or name a command that does not exist are
skipped and logged.

```json
{
  "bindings": [
    { "keys": "X", "command": "clip.remove", "args": { "clipId": "$selection", "ripple": true } },
    { "keys": "Delete", "command": "" }
  ]
}
```

## Coming in later phases

| Phase | Adds |
|---|---|
| 25 | `jazz rpc call`, `rpc list`, `rpc events`, `--attach` |
| 26 | `jazz mcp` |
