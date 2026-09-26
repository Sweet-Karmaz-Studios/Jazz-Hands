# jazz command reference

This file is generated: `jazz docs --markdown --out Docs/CLI.md` writes it from the commands
themselves, so every verb, argument and option here is the one `jazz` parses, with the
description `--help` prints. Change a description in the code, or the prose in `Docs/cli`
(`intro.md`, `exit-codes.md`, `guides.md`, and a note per verb in `notes/`), and regenerate. A
test fails when this file is out of date.

See the `cli-conventions` skill for the rules the CLI follows, and "Guides" at the end for how
the verbs fit together.

```
jazz <verb> [arguments] [--options]
jazz <area> <verb> <project.jazz> [arguments] [--options]
```

Most verbs are generated from the command registry: a command or query `area.verb` is
`jazz area verb`, spelled as it is over JSON-RPC and to MCP. The project comes first. Queries
print their answer as readable text, and as JSON with `--json`. Times take every form
`Timecode.Parse` reads (`00:00:04:12`, `00:00:04.500`, `4.5s`, `135f`, `3175200000fl`), at the
active sequence's rate.

## Global options

| Option | Meaning |
|---|---|
| `--help`, `-?`, `-h`, `/?`, `/h` | Show help and usage information. |
| `--version` | Show version information. |
| `--json` | Emit a single JSON object instead of human-readable text. |
| `--verbose`, `-v` | Log engine detail to stderr. |
| `--gpu <gpu>` | Which adapter renders: auto (the best GPU, the default), warp (the software rasterizer, as tests and CI use) or an adapter number from 'jazz version'. JAZZ_GPU sets the same. |
| `--attach` | Send the command to a running editor or 'jazz serve' instead of opening a project, which is then left out: the newest one, or --attach pipe:<name>, a process id, or 127.0.0.1:47800 for TCP. |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | Command error: validation failed, id not found, nothing to do, a script step failed. |
| 2 | Usage error: unknown verb, bad option, missing argument. |
| 3 | Media error: unreadable file, unsupported codec, encoder failure. |
| 4 | Attach failed: no running instance answered. |
| 130 | Cancelled with Ctrl+C. |

## Verbs

### `jazz version`

Print the Jazz Hands, .NET, FFmpeg and GPU versions.

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

### `jazz new <project>`

Create an empty project with one video and one audio track.

| Argument | Meaning |
|---|---|
| `<project>` | Where to write the new .jazz file. |

| Option | Meaning |
|---|---|
| `--fps <fps>` | Frame rate: 30, 60, an exact ratio such as 30000/1001, or one of the decimal shorthands 23.976, 29.97, 59.94, 119.88. Any other decimal is refused, with the ratio to type instead. Default: 30. |
| `--size <size>` | Frame size: 1920x1080, or one of 720p, 1080p, 2k, 4k, 8k. Default: 1920x1080. |
| `--name <name>` | The project name. Defaults to the file name. |
| `--force` | Overwrite an existing project file. |

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

### `jazz validate <project>`

Check a project against the schema and the semantic rules.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to check. |

| Option | Meaning |
|---|---|
| `--strict` | Treat warnings as failures. |

Checks a project against the generated schema and then against the semantic rules, and prints one
line per problem as `path: severity: code: message`. Errors go to stderr, warnings to stdout.

```bash
jazz validate trailer.jazz
```

```
/sequences/0/tracks/0/kind: error: not-a-member: Value should match one of the values specified by the enum
```

Exit code 0 when the project loads, 1 when it does not, or when `--strict` and anything at all was
found. This is the only verb that runs schema validation; the others skip it because it costs more
than the rest of opening a project put together.

With `--json`: one object with `loadable`, `errors`, `warnings` and the `issues` array.

### `jazz fmt <project>`

Rewrite a project into canonical form: ordering, indentation, defaults.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to rewrite. |

| Option | Meaning |
|---|---|
| `--check` | Report whether the file is already canonical and exit 1 if not, without writing. |

Rewrites a project into canonical form: declaration order, two space indentation, line feeds,
defaults left out, clips in start order and tracks in stacking order. Members this build does not
recognise are kept exactly as they were.

```bash
jazz fmt trailer.jazz --check
```


With `--json`: `{"path": ..., "rewritten": true}`.

### `jazz repair <project>`

Fix the problems with one obvious answer: dangling references, zero speeds, fades that overrun.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to repair. |

| Option | Meaning |
|---|---|
| `--dry-run` | Report what would change without writing anything. |

Fixes the problems that have one obvious answer and reports the rest.

```bash
jazz repair trailer.jazz --dry-run
```

It removes clips whose media or nested sequence is not in the project, removes transitions whose
clips are gone, puts a zero speed back to 1/1, moves a clip that starts before the timeline, and
shortens fades that overrun their clip. It does not touch overlapping clips, because trimming
either one, moving either one and deleting either one are all defensible and a tool that guesses
is a tool nobody dares run.

Exit code 1 if an error-level problem is still there afterwards.

With `--json`: the `actions` taken and what is `remaining`.

### `jazz frame <project>`

Draw one frame of a sequence to a PNG exactly as the editor's preview shows it, to look at, or with --sheet several across a clip. 'jazz export still' writes the frame an export would.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file. |

| Option | Meaning |
|---|---|
| `--at <at>` | The sequence time to draw: 00:00:12.500, 750f or 12.5s. Required without --sheet. |
| `--out <out>` | Required. The .png to write, relative to the current folder. |
| `--size <size>` | How wide to draw it, as a size: 960x540 or 1080p; the height follows the sequence's shape. The sequence's own when left out; 1920 wide for a sheet. |
| `--sequence <sequence>` | Which sequence; the active one when left out. |
| `--sheet` | Draw several frames side by side, labelled with their times: an animated effect or a template judged from one picture. |
| `--times <times>` | With --sheet: fractions of the way through, 0 the start and 1 the last frame. Default: 0,0.25,0.5,0.75,1. |
| `--clip <clip>` | With --sheet: through this clip rather than the whole sequence. |

### `jazz frames <project>`

Draw a frame every so often through a sequence as the preview shows it, one PNG each, named by their times.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file. |

| Option | Meaning |
|---|---|
| `--every <every>` | Required. How far apart: 2s, 00:00:05.000, 120f. |
| `--out <out>` | Required. The folder to write the PNGs into; made when it is not there. |
| `--size <size>` | How wide to draw each, as a size: 640x360; the height follows the sequence's shape. Default: 640x360. |
| `--sequence <sequence>` | Which sequence; the active one when left out. |
| `--range <range>` | Only this stretch: 00:10-00:30. All of the sequence when left out. |

### `jazz contact-sheet <project>`

Tile frames from even steps through a sequence into one PNG, each labelled with its time: a whole edit at a glance.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file. |

| Option | Meaning |
|---|---|
| `--out <out>` | Required. The .png to write. |
| `--cols <cols>` | Tiles across. Default: 6. |
| `--rows <rows>` | Tiles down. Default: 4. |
| `--width <width>` | The sheet's width in pixels. Default: 1920. |
| `--sequence <sequence>` | Which sequence; the active one when left out. |
| `--range <range>` | Only this stretch: 00:10-00:30. |

### `jazz proof <project>`

Export a quick 480p proof of a sequence, every frame rendered, to watch before handing off.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file. |

| Option | Meaning |
|---|---|
| `--out <out>` | Required. Where to write it, relative to the project: proof.mp4. |
| `--sequence <sequence>` | Which sequence; the active one when left out. |
| `--range <range>` | Only this stretch: 00:10-00:30. |
| `--encoder <encoder>` | The encoders to try, in order: libx264 keeps it off the GPU. |

### `jazz apply <project> <script>`

Run a script of commands against a project in one session; save only if every step worked.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to change. |
| `<script>` | The script: a JSON array of { "command", "args", "as" } steps. |

| Option | Meaning |
|---|---|
| `--continue` | Run every step even after one fails, and save what worked. |
| `--dry-run` | Run the steps that only change the project, report, and save nothing. |
| `--no-save` | Do not write the project back. |

### `jazz docs`

Write the command reference, Docs/CLI.md, from the commands themselves.

| Option | Meaning |
|---|---|
| `--markdown` | Write the reference as Markdown. The only form there is, and the default. |
| `--out <out>` | Write it to this file, as UTF-8, instead of to the console: Docs/CLI.md. |

### `jazz serve <project>`

Open a project headless and serve it over the control server until Ctrl+C: a GUI-less editor for scripts and Claude Code.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to serve; made empty when it is not there. |

| Option | Meaning |
|---|---|
| `--pipe <pipe>` | The pipe name. 'jazzhands' when free, else 'jazzhands-<pid>'. |
| `--tcp` | Listen on TCP too, on loopback, with a token clients must say. |
| `--port <port>` | The TCP port; 0 picks a free one. Default: 47800. |
| `--address <address>` | The address TCP binds to. Other than loopback needs --allow-remote. Default: 127.0.0.1. |
| `--allow-remote` | Let TCP bind to an address other machines can reach. Only on a network you trust; the token is all that stands between it and anyone. |
| `--token <token>` | The token TCP clients must say; made up when left out, and printed. |
| `--save-on-exit` | Save the project when stopped with Ctrl+C, if it has changes. |

### `jazz mcp`

Serve the Model Context Protocol on stdin and stdout for Claude Code: 'claude mcp add jazz -- jazz mcp --attach'.

| Option | Meaning |
|---|---|
| `--project <project>` | Open this project here instead of attaching to an editor; a path that does not exist yet starts a new project there. |
| `--save-on-exit` | With --project, save it when the client disconnects, if it changed. |
| `--list-tools` | Print the tools and what each does, then exit. With --json, their schemas too. |

### `jazz describe <project>`

Describe the project for a person or a model: settings, media, a sequence's tracks and clips in order, gaps, transitions, markers and problems.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--sequence <id>` | Which sequence; the active one when left out. |
| `--range <range>` | Only what is inside this stretch: 00:10-00:25. |
| `--detail <brief|full>` | brief (the default, budgeted) or full (ids, sources, effects, problems). Default: brief. |
| `--budget <n>` | At brief, about the most tokens to spend; 0 for no limit. Default: 2000. |

### `jazz redo <project>`

Put back a command that was undone.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--steps <n>` | How many commands to put back. Default: 1. |
| `--no-save` | Do not write the project back. |

### `jazz undo <project>`

Take back the last command.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--steps <n>` | How many commands to take back. Default: 1. |
| `--no-save` | Do not write the project back. |

## `jazz app`

Commands and queries about app.

### `jazz app hide <project>`

Hide the editor to the notification area, where it keeps running.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz app quit <project>`

Quit the editor.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--force` | Quit even with unsaved changes or a running export. |
| `--wait-for-exports` | Let the exports finish first, then quit. |
| `--no-save` | Do not write the project back. |

### `jazz app show <project>`

Show the editor's window and bring it to the front.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz app unregister <project>`

Remove Jazz Hands from Windows (file association, Explorer verbs, start with Windows) before uninstalling.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

## `jazz audio`

Sound on clips: gain, pan, fades, channel maps, muting a stream, detaching sound; the master volume and limiter; the meters.

### `jazz audio beat-analysis <project> <clip-id>`

The tempo, confidence and beats of a music clip, without marking them.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The music clip. |

### `jazz audio beats <project> <clip-id>`

Find a music clip's beats and mark them on the sequence.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The music clip. |

| Option | Meaning |
|---|---|
| `--keep` | Keep beat markers already there. |
| `--no-save` | Do not write the project back. |

### `jazz audio detach <project> <clip-id>`

Unlink a clip's audio from its picture.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id, or any clip linked to it. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz audio duck <project>`

Duck the music under a voice: one track drops by a depth while another has sound, holding over the gaps between words.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--music <id>` | Required. The track to turn down. |
| `--voice <id>` | The track that turns it down. |
| `--depth <number>` | How far down: -12dB. Default: -12. Default: -12. |
| `--threshold <number>` | How loud the voice must be to duck, in dBFS. Default: -40. Default: -40. |
| `--attack <time>` | How quickly it goes down: 15ms. Default: 15 ms. |
| `--hold <time>` | How long it stays down after the voice stops. Default: 300 ms. |
| `--release <time>` | How quickly it comes back. Default: 400 ms. |
| `--off` | Take the ducking off the music track. |
| `--no-save` | Do not write the project back. |

### `jazz audio learn-noise <project> <clip-id>`

Learn a clip's background noise (a fan, hiss, the room) from a stretch with only noise in it, and take it out with noise reduction.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | A sound clip, or a video clip for its linked sound. |

| Option | Meaning |
|---|---|
| `--from <time>` | Where the stretch of noise starts, on the sequence. Default: the clip's quietest half second. |
| `--to <time>` | Where it ends. |
| `--no-save` | Do not write the project back. |

### `jazz audio measure <project>`

Measure a clip, a track or the mix on its own: peak, RMS and integrated loudness (LUFS).

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--clip <id>` | A clip, or its linked sound for a video clip. |
| `--track <id>` | A track. |
| `--mix` | The whole mix, before the limiter. |
| `--sequence <id>` | For --mix, which sequence. |

### `jazz audio meter <project>`

Meter a stretch of the mix: peak, true peak, RMS and LUFS.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--from <time>` | Where to start on the sequence. |
| `--to <time>` | Where to stop. |
| `--sequence <id>` | Which sequence. |

### `jazz audio mute-stream <project> <clip-id>`

Mute one audio stream of a clip.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id, or any clip linked to it. |

| Option | Meaning |
|---|---|
| `--stream <n>` | Which stream of the media. |
| `--muted` | true to mute, false to unmute. Default: true. |
| `--no-save` | Do not write the project back. |

### `jazz audio normalize <project>`

Set a clip's, a track's or the mix's gain so it measures a peak, RMS or loudness (LUFS) target.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--clip <id>` | A clip: its volume, or its linked sound's for a video clip. |
| `--track <id>` | A track: its fader. |
| `--mix` | The whole mix, by the master volume. |
| `--mode <peak|rms|lufs>` | peak, rms or lufs. Default: lufs. Default: lufs. |
| `--target <number>` | dBFS for peak and rms, LUFS for lufs. Default: -1, -20 and -14. |
| `--sequence <id>` | For --mix, which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz audio reduce-noise <project> <clip-id>`

Turn a clip's noise reduction on, change how much it takes out, or take it off.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | A sound clip, or a video clip for its linked sound. |

| Option | Meaning |
|---|---|
| `--reduction <number>` | How far the noise goes down at most: 12dB. Default: 12. |
| `--sensitivity <number>` | How far over the noise a sound must be to stay, 0.5 to 4. Default: 2. |
| `--off` | Take noise reduction off. |
| `--no-save` | Do not write the project back. |

### `jazz audio replace <project> <clip-id> <media-id>`

Play another media item's audio in a clip.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |
| `<media-id>` | The media item to play instead. |

| Option | Meaning |
|---|---|
| `--stream <n>` | Which audio stream of it; the first when left out. |
| `--no-save` | Do not write the project back. |

### `jazz audio set-channel-map <project> <clip-id> <map>`

Play one channel of a stereo clip as mono.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |
| `<map>` | auto, left, right or mono. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz audio set-fade-in <project> <clip-id>`

Set an audio clip's fade in.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--dur <time>` | Required. How long the fade lasts; 0 removes it. |
| `--curve <hold|linear|bezier|ease-in|ease-out|ease-in-out>` | linear, ease-in-out (smooth), ease-in (slow start), ease-out (fast start) or bezier (S curve). Default: linear. |
| `--no-save` | Do not write the project back. |

### `jazz audio set-fade-out <project> <clip-id>`

Set an audio clip's fade out.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--dur <time>` | Required. How long the fade lasts; 0 removes it. |
| `--curve <hold|linear|bezier|ease-in|ease-out|ease-in-out>` | linear, ease-in-out (smooth), ease-in (slow start), ease-out (fast start) or bezier (S curve). Default: linear. |
| `--no-save` | Do not write the project back. |

### `jazz audio set-gain <project> <clip-id>`

Set an audio clip's gain in dB, or a keyframe of it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--db <number>` | Required. Gain in dB, -144 to 24. |
| `--at <time>` | Set a keyframe at this time on the sequence. |
| `--no-save` | Do not write the project back. |

### `jazz audio set-limiter <project>`

Turn the master limiter on or off, or set its ceiling in dBTP.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--on` | true to turn it on, false to turn it off. |
| `--ceiling <number>` | The ceiling in dBTP, -24 to 0. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz audio set-master-volume <project>`

Set a sequence's master volume in dB, or a keyframe of it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--db <number>` | Required. Volume in dB, -144 to 24. |
| `--at <time>` | Set a keyframe at this time on the sequence. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz audio set-pan <project> <clip-id>`

Set an audio clip's pan, or a keyframe of it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--pan <number>` | Required. -1 hard left, 0 centre, 1 hard right. |
| `--at <time>` | Set a keyframe at this time on the sequence. |
| `--no-save` | Do not write the project back. |

### `jazz audio sync <project>`

Move a clip so its sound lines up with another clip's: a separate microphone with the camera, by their waveforms.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--clip <id>` | Required. The clip to move. |
| `--to <id>` | Required. The clip to line it up with. |
| `--force` | Move it even when the match is unsure. |
| `--no-save` | Do not write the project back. |

### `jazz audio sync-offset <project>`

How far to move a clip so its sound lines up with another clip's, and how sure the match is.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--clip <id>` | Required. The clip that would move. |
| `--to <id>` | Required. The clip it lines up with. |

## `jazz cache`

The thumbnail, waveform and proxy cache: what it holds, its size limit, emptying it.

### `jazz cache clear <project>`

Empty the cache, or parts of it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--media <id>` | Only what is cached about this media id. |
| `--thumbs` | Thumbnails. |
| `--waveforms` | Waveforms. |
| `--probes` | Probes. |
| `--keyframes` | Keyframe indexes. |
| `--proxies` | Proxy files. |
| `--all` | Everything, proxies included. |
| `--no-save` | Do not write the project back. |

### `jazz cache configure <project>`

Set the cache's folder and size limit.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--location <location>` | The cache folder, used from the next start. |
| `--cap-gb <number>` | The size limit in gigabytes; 0 for none. |
| `--no-save` | Do not write the project back. |

### `jazz cache stats <project>`

Show what the cache holds and its size limit.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

## `jazz chapter`

Chapters: markers that become a file's chapters, from markers or imported from media.

### `jazz chapter add <project> <name>`

Put a chapter mark on a sequence.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<name>` | Its title. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. Where it starts. |
| `--sequence <id>` | Which sequence. |
| `--id <id>` | The identifier to give it. |
| `--no-save` | Do not write the project back. |

### `jazz chapter from-markers <project>`

Make every marker on a sequence a chapter.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz chapter import <project> <media-id>`

Make a media item's chapters into chapter marks.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<media-id>` | The media item id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Where the file's start lands. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz chapter list <project>`

List a sequence's chapters.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--sequence <id>` | Which sequence. |

## `jazz clip`

Clips on the timeline: add, move, trim, split, ripple, roll, slip, slide, speed, freeze and delete.

### `jazz clip add <project> <track-id>`

Put a clip on a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. Where it starts on the timeline. |
| `--media <id>` | The media item to play. |
| `--generator <id>` | The generator type, for example text.title. |
| `--sequence <id>` | The sequence to nest. |
| `--in <time>` | Where playback starts inside the source. |
| `--dur <time>` | How long it runs. |
| `--name <name>` | Its display name. |
| `--stream <n>` | Which stream of the media to play; the first of the right kind when left out. |
| `--id <id>` | The identifier to give it. |
| `--audio` | Also put each audio stream on an audio track, linked to the picture. Default: true. |
| `--no-save` | Do not write the project back. |

### `jazz clip close-gap <project> <track-id>`

Close a gap on a track, rippling every sync-locked track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. A time inside the gap. |
| `--no-save` | Do not write the project back. |

### `jazz clip copy <project> <clip-id>`

Copy a clip to another time or track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. Where the copy should start. |
| `--track <id>` | Which track to put it on. |
| `--id <id>` | The identifier for the copy. |
| `--no-save` | Do not write the project back. |

### `jazz clip duplicate <project> <clip-id>`

Copy a clip to immediately after itself.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--id <id>` | The identifier for the copy. |
| `--no-save` | Do not write the project back. |

### `jazz clip extract <project>`

Remove a range and close the gap.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--from <time>` | Required. Where the range starts. |
| `--to <time>` | Required. Where it ends. |
| `--tracks <list>` | Comma-separated track ids; every unlocked track when left out. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz clip freeze-frame <project> <clip-id>`

Hold a frame for a while, pushing the rest on.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The frame to hold. |
| `--dur <time>` | How long to hold it; two seconds when left out. |
| `--id <id>` | The identifier for the freeze frame. |
| `--no-save` | Do not write the project back. |

### `jazz clip get <project> <clip-id>`

Describe one clip.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

### `jazz clip group <project> <clip-ids>`

Group clips so that selecting one selects them all.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-ids>` | Comma-separated clip ids. |

| Option | Meaning |
|---|---|
| `--id <id>` | The identifier to share. |
| `--no-save` | Do not write the project back. |

### `jazz clip insert <project> <track-id>`

Put a clip in at a time, pushing the rest on.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. Where it starts on the timeline. |
| `--media <id>` | The media item to play. |
| `--generator <id>` | The generator type, for example text.title. |
| `--sequence <id>` | The sequence to nest. |
| `--in <time>` | Where playback starts inside the source. |
| `--dur <time>` | How long it runs. |
| `--name <name>` | Its display name. |
| `--stream <n>` | Which stream of the media to play; the first of the right kind when left out. |
| `--id <id>` | The identifier to give it. |
| `--audio` | Also put each audio stream on an audio track, linked to the picture. Default: true. |
| `--no-save` | Do not write the project back. |

### `jazz clip lift <project>`

Remove a range, leaving a gap.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--from <time>` | Required. Where the range starts. |
| `--to <time>` | Required. Where it ends. |
| `--tracks <list>` | Comma-separated track ids; every unlocked track when left out. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz clip link <project> <clip-ids>`

Link clips so that moving one moves them all.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-ids>` | Comma-separated clip ids. |

| Option | Meaning |
|---|---|
| `--id <id>` | The identifier to share. |
| `--no-save` | Do not write the project back. |

### `jazz clip list <project>`

List clips, in timeline order.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--track <id>` | Only this track. |
| `--sequence <id>` | Which sequence. |

### `jazz clip match-frame <project> <clip-id>`

Find the source and frame a clip shows at a time.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The time on the timeline. |

### `jazz clip move <project> <clip-id>`

Move a clip to another time or track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--to <time>` | Required. Where it should start. |
| `--track <id>` | Which track to move it to. |
| `--no-save` | Do not write the project back. |

### `jazz clip nest <project> <clip-ids> <name>`

Move clips into a new sequence and leave a compound clip behind.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-ids>` | Comma-separated clip ids. |
| `<name>` | The name of the new sequence. |

| Option | Meaning |
|---|---|
| `--id <id>` | The identifier for the compound clip. |
| `--no-save` | Do not write the project back. |

### `jazz clip nudge <project> <clip-ids>`

Move clips earlier or later by whole frames.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-ids>` | Comma-separated clip ids. |

| Option | Meaning |
|---|---|
| `--frames <n>` | How many frames; negative moves earlier. Default: 1. |
| `--no-save` | Do not write the project back. |

### `jazz clip overwrite <project> <track-id>`

Put a clip over whatever is at a time.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. Where it starts on the timeline. |
| `--media <id>` | The media item to play. |
| `--generator <id>` | The generator type, for example text.title. |
| `--sequence <id>` | The sequence to nest. |
| `--in <time>` | Where playback starts inside the source. |
| `--dur <time>` | How long it runs. |
| `--name <name>` | Its display name. |
| `--stream <n>` | Which stream of the media to play; the first of the right kind when left out. |
| `--id <id>` | The identifier to give it. |
| `--audio` | Also put each audio stream on an audio track, linked to the picture. Default: true. |
| `--no-save` | Do not write the project back. |

### `jazz clip paste <project>`

Paste copied clips at a time, over what is there or pushing it on.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--data <data>` | Required. What clipboard.copy returned. |
| `--at <time>` | Required. Where the earliest clip goes. |
| `--track <id>` | The track for the lowest copied track of its kind. |
| `--insert` | Push what is there on instead of pasting over it. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz clip ramp-speed <project> <clip-id>`

Ramp a clip from one speed to another.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--from <number>` | Required. The speed it starts at: 1 is normal. |
| `--to <number>` | Required. The speed it ends at. |
| `--at <time>` | Required. When the ramp starts, on the sequence. |
| `--dur <time>` | Required. How long the ramp takes. |
| `--linear` | A constant change rather than an ease. |
| `--no-save` | Do not write the project back. |

### `jazz clip rate-stretch <project> <clip-id>`

Stretch a clip to a new duration by changing its speed.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--dur <time>` | Required. How long it should become. |
| `--no-save` | Do not write the project back. |

### `jazz clip remove <project> <clip-id>`

Remove a clip, leaving a gap or closing it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--ripple` | Close the gap behind it. |
| `--no-save` | Do not write the project back. |

### `jazz clip ripple-delete <project> <clip-ids>`

Remove clips and close the gap on every sync-locked track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-ids>` | Comma-separated clip ids. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz clip ripple-trim <project> <clip-ids>`

Trim a clip edge and ripple everything after it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-ids>` | Comma-separated clip ids. |

| Option | Meaning |
|---|---|
| `--edge <none|start|end>` | Required. start or end. |
| `--to <time>` | Required. Where the edge goes. |
| `--no-save` | Do not write the project back. |

### `jazz clip roll <project> <left-clip-id> <right-clip-id>`

Move the cut between two touching clips.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<left-clip-id>` | The outgoing clip id. |
| `<right-clip-id>` | The incoming clip id. |

| Option | Meaning |
|---|---|
| `--by <time>` | Required. How far to move the cut. |
| `--no-save` | Do not write the project back. |

### `jazz clip set-blend <project> <clip-id> <mode>`

Set how a clip blends with what is under it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |
| `<mode>` | normal, add, multiply, screen, overlay, darken, lighten, difference, soft-light or hard-light. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz clip set-crop <project> <clip-id>`

Cut away the edges of a clip's picture.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--left <number>` | Percent off the left. |
| `--top <number>` | Percent off the top. |
| `--right <number>` | Percent off the right. |
| `--bottom <number>` | Percent off the bottom. |
| `--no-save` | Do not write the project back. |

### `jazz clip set-enabled <project> <clip-id> <enabled>`

Enable or disable a clip without removing it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |
| `<enabled>` | false to disable. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz clip set-matte <project> <clip-id>`

Show a clip only through another track's picture (a track matte).

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--source <source>` | The track whose picture is the matte. |
| `--mode <alpha|luma|alpha-inverted|luma-inverted>` | alpha, luma, alpha-inverted or luma-inverted. Default: alpha. Default: alpha. |
| `--off` | Take the clip's matte away. |
| `--no-save` | Do not write the project back. |

### `jazz clip set-motion-blur <project> <clip-id>`

Set motion blur on a clip's animated movement.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--angle <number>` | Shutter angle in degrees: 180 is half a frame. Default: 180. |
| `--samples <n>` | Moments averaged, 2 to 64. Default: 32. |
| `--off` | Turn it off for this clip. |
| `--inherit` | Follow the track and sequence instead. |
| `--no-save` | Do not write the project back. |

### `jazz clip set-opacity <project> <clip-id>`

Set how opaque a clip's picture is.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--opacity <number>` | Required. 0 invisible to 1 opaque. |
| `--no-save` | Do not write the project back. |

### `jazz clip set-remap <project> <clip-id>`

Turn time remapping (a speed curve) on or off for a clip.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--off` | Turn it off instead. |
| `--no-save` | Do not write the project back. |

### `jazz clip set-retime <project> <clip-id> <mode>`

Set how a clip shows moments between source frames: nearest frame or a blend.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |
| `<mode>` | nearest, blend or optical-flow (which blends in this version). |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz clip set-reverse <project> <clip-id> <reverse>`

Play a clip backwards.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |
| `<reverse>` | true to play it backwards. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz clip set-speed <project> <clip-id>`

Change how fast a clip plays.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--speed <rate>` | Required. The rate, for example 2 or 1/2. |
| `--keep-duration` | Keep the timeline duration. |
| `--no-save` | Do not write the project back. |

### `jazz clip set-tone-map <project> <clip-id>`

Set how a clip's HDR picture is brought down to SDR.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--operator <bt2390|hable|mobius|clip>` | bt2390, hable, mobius or clip. |
| `--peak <number>` | The source's peak in nits, overriding the file's; 100 to 10000. |
| `--desaturate <number>` | How much compressed highlights lose their colour, 0 to 1. |
| `--reset` | Follow the project's default again. |
| `--no-save` | Do not write the project back. |

### `jazz clip set-transform <project> <clip-id>`

Move, scale or rotate a clip's picture.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--x <number>` | Pixels right of the frame centre. |
| `--y <number>` | Pixels below the frame centre. |
| `--scale <number>` | Both axes; 1 is the fitted size. |
| `--scale-x <number>` | The horizontal scale alone. |
| `--scale-y <number>` | The vertical scale alone. |
| `--rotation <number>` | Degrees clockwise. |
| `--anchor-x <number>` | The pivot, picture pixels right of its centre. |
| `--anchor-y <number>` | The pivot, picture pixels below its centre. |
| `--no-save` | Do not write the project back. |

### `jazz clip slide <project> <clip-id>`

Move a clip, taking the time out of its neighbours.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--by <time>` | Required. How far to move it. |
| `--no-save` | Do not write the project back. |

### `jazz clip slip <project> <clip-id>`

Change which part of the source a clip shows.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--by <time>` | Required. How far through the source to slide. |
| `--no-save` | Do not write the project back. |

### `jazz clip speed-preset <project> <clip-id> <preset>`

Put a packaged speed ramp on a clip: slow into an impact and snap back, speed through a traversal, a VHS rewind, or a slow motion replay.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |
| `<preset>` | impact, traversal, rewind or replay. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The moment: the hit, the start, or the end of what is repeated. |
| `--dur <time>` | How long it runs fast, or how much is repeated. Default: 2s. |
| `--no-save` | Do not write the project back. |

### `jazz clip split <project> <clip-id>`

Cut a clip in two at a timeline time.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. Where to cut, on the timeline. |
| `--id <id>` | The identifier for the right half. |
| `--no-save` | Do not write the project back. |

### `jazz clip stabilize <project> <clip-id>`

Steady a shaky clip (analyses its motion the first time).

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--smoothing <n>` | Frames either side the camera path is smoothed over. Default: 15. Default: 15. |
| `--zoom <number>` | Extra zoom in percent. Default: 0. Default: 0. |
| `--no-auto-zoom` | Do not zoom in to hide the moving edges. |
| `--reanalyze` | Analyse the file again. |
| `--off` | Remove the stabilization instead. |
| `--no-save` | Do not write the project back. |

### `jazz clip storyline-move <project> <clip-ids>`

Move clips along the primary track, closing up behind them.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-ids>` | Comma-separated clip ids. |

| Option | Meaning |
|---|---|
| `--to <time>` | Required. Where the first of them was dropped. |
| `--no-save` | Do not write the project back. |

### `jazz clip trim <project> <clip-id>`

Move a clip start or end.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--in <time>` | Where the clip should start. |
| `--out <time>` | Where the clip should end. |
| `--ripple` | Move everything after it too. |
| `--no-save` | Do not write the project back. |

### `jazz clip ungroup <project> <clip-ids>`

Take clips out of their selection group.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-ids>` | Comma-separated clip ids. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz clip unlink <project> <clip-ids>`

Break the sync lock on clips.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-ids>` | Comma-separated clip ids. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz clip unnest <project> <clip-id>`

Replace a compound clip with the clips inside it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The compound clip id. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

## `jazz clipboard`

Clips as JSON, to paste with 'clip paste' here or in another project.

### `jazz clipboard copy <project> <clip-ids>`

Copy clips as JSON for clip.paste.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-ids>` | Comma-separated clip ids. |

## `jazz color`

Colour: sample the picture's colour at a point. Tone mapping is on clips and the project.

### `jazz color sample <project>`

Read the picture's colour at a point.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. When, on the timeline. |
| `--x <number>` | Across, in sequence pixels from the frame centre. Default: 0. |
| `--y <number>` | Down, in sequence pixels from the frame centre. Default: 0. |
| `--before <id>` | An effect to read the picture before. |
| `--size <n>` | The side of the square averaged, in pixels. Default: 5. |
| `--sequence <id>` | Which sequence. |

## `jazz diagnostics`

What the session has noticed: fallbacks and missing files.

### `jazz diagnostics list <project>`

List what the session has noticed: fallbacks, missing files.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--media <id>` | Only this media item. |
| `--level <information|warning|error>` | information, warning or error. Default: information. |

## `jazz edit`

Commands and queries about edit.

### `jazz edit cut-to-beats <project>`

Lay clips along the beat markers, one to each stretch between cuts.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--track <id>` | Required. The video track to lay them on. |
| `--every <n>` | Cut on every this many beats. Default: 1. Default: 1. |
| `--downbeats` | Cut only on downbeats. |
| `--from <time>` | The first cut is at or after this. |
| `--to <time>` | The last cut is at or before this. |
| `--clips <list>` | Clips to lay, in order. |
| `--bin <bin>` | A media folder to lay instead, by name. |
| `--with-audio` | Bring each clip's sound along. |
| `--no-save` | Do not write the project back. |

## `jazz effect`

Effects on clips and tracks: add, set parameters, bypass, reorder, presets. 'effect list' names every type.

### `jazz effect add <project> <owner-id> <type-id>`

Add an effect to a clip or a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip or track id. |
| `<type-id>` | The effect type, for example video.blur.gaussian. |

| Option | Meaning |
|---|---|
| `--index <n>` | Where in the chain, from 0; the end when not given. |
| `--id <id>` | The identifier for the new effect. |
| `--no-save` | Do not write the project back. |

### `jazz effect apply-preset <project> <owner-id> <preset>`

Apply an effect preset to a clip or a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip or track id. |
| `<preset>` | The preset's id or name. |

| Option | Meaning |
|---|---|
| `--index <n>` | Where in the chain, from 0; the end when not given. |
| `--no-save` | Do not write the project back. |

### `jazz effect copy <project> <ids>`

Copy effects as JSON for effect.paste.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<ids>` | Comma-separated effect ids, or a clip or track id for its whole chain. |

### `jazz effect export-preset <project> <preset>`

An effect preset as JSON.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<preset>` | The preset's id or name. |

### `jazz effect get <project> <effect-id>`

Show one effect and its parameters.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<effect-id>` | The effect id. |

### `jazz effect import-preset <project>`

Add an effect preset from JSON.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--data <data>` | Required. What effect.export-preset returned. |
| `--name <name>` | A new name; its own when not given. |
| `--no-save` | Do not write the project back. |

### `jazz effect list`

List the effect types and their parameters.

| Option | Meaning |
|---|---|
| `--project <project>` | A .jazz file to read alongside, for its fonts folder and relative paths. |
| `--kind <video|audio|generator|transition|audio-transition|audio-generator>` | Only video, audio or generator. |
| `--search <search>` | Only types whose id, name or category contain this. |

### `jazz effect list-presets <project>`

List the effect presets in the project.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz effect move <project> <effect-id>`

Move an effect up or down its chain.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<effect-id>` | The effect id. |

| Option | Meaning |
|---|---|
| `--index <n>` | Required. Its new place in the chain, from 0. |
| `--no-save` | Do not write the project back. |

### `jazz effect paste <project> <owner-id>`

Paste copied effects onto a clip or a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip or track id. |

| Option | Meaning |
|---|---|
| `--data <data>` | Required. What effect.copy returned. |
| `--index <n>` | Where in the chain, from 0; the end when not given. |
| `--no-save` | Do not write the project back. |

### `jazz effect remove <project> <effect-id>`

Remove an effect.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<effect-id>` | The effect id. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz effect remove-preset <project> <preset>`

Delete an effect preset.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<preset>` | The preset's id or name. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz effect reset <project> <effect-id>`

Put an effect's parameters back to their defaults.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<effect-id>` | The effect id. |

| Option | Meaning |
|---|---|
| `--param <param>` | Only this parameter; all of them when not given. |
| `--no-save` | Do not write the project back. |

### `jazz effect save-preset <project> <ids>`

Save an effect chain as a preset.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<ids>` | Comma-separated effect ids, or a clip or track id for its whole chain. |

| Option | Meaning |
|---|---|
| `--name <name>` | Required. What to call the preset. |
| `--id <id>` | The identifier for the preset. |
| `--no-save` | Do not write the project back. |

### `jazz effect set-enabled <project> <effect-id> <enabled>`

Bypass an effect or turn it back on.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<effect-id>` | The effect id. |
| `<enabled>` | true to run it, false to bypass it. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz effect set-param <project> <effect-id> <param> <value>`

Set a parameter of an effect.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<effect-id>` | The effect id. |
| `<param>` | The parameter name. |
| `<value>` | The value, for example 12, '100, 50' or #FF8800. |

| Option | Meaning |
|---|---|
| `--at <time>` | For a keyframed parameter, the keyframe's time on the sequence. |
| `--local` | Read --at from the clip's start rather than the sequence's. |
| `--no-save` | Do not write the project back. |

## `jazz export`

### `jazz export <project>`

Export a sequence to a file in the foreground. 'jazz export enqueue' queues one in a running editor.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to export. |

| Option | Meaning |
|---|---|
| `--out <out>` | Required. Where to write the file. Relative to the project. The extension picks the container; left off, the preset's is added. |
| `--preset <preset>` | Which preset: youtube-1080p (the default) or any other; 'jazz presets list' shows them, built in and your own. Default: youtube-1080p. |
| `--mode <mode>` | auto copies when the timeline plays one file untouched and the preset would write what the source already is, smart cuts when such a timeline's cuts are off keyframes, and encodes otherwise. smart and copy keep the source's codec whatever the preset says. full (or encode) renders every frame. Default: auto. |
| `--sequence <sequence>` | Which sequence. The active one when left out. |
| `--snap-to-keyframes` | For a copy, move cuts to the nearest keyframe instead of refusing. |
| `--use-in-out` | Export only between the sequence's in and out points. |
| `--use-external-ffmpeg` | Encode through ffmpeg.exe. |
| `--subtitles <subtitles>` | What subtitle tracks become. soft (the default): a stream per track that players turn on and off, mov_text in MP4, SubRip or ASS (for a styled track or placed cues) in Matroska, the first on by default. burn: drawn into the picture, which makes the export an encode. sidecar: a file per track beside the video, trailer.eng.srt. none: left out. ffmpeg.exe exports write sidecars for soft. Default: soft. |
| `--sidecar-format <sidecar-format>` | srt, vtt or ass, for --subtitles sidecar. Default: srt. |
| `--no-chapters` | Leave the chapter marks out. They go in by default: Matroska chapters, and an MP4 chapter track and chpl box, which YouTube reads. |
| `--dry-run` | Print the plan, with the reasons for the mode and an estimate of size and time, and write nothing. |
| `--size <size>` | Fit the picture inside this size, for example 1280x720. Never scales up. |
| `--fps <fps>` | Write at this frame rate, for example 30 or 30000/1001. A slower rate takes every nth frame. |
| `--quality <quality>` | Constant quality: CRF or CQ, lower is better. |
| `--bitrate <bitrate>` | A picture bitrate instead of constant quality: 8M, 2500k. |
| `--encoder <encoder>` | The encoders to try, in order, comma separated: libx264 or hevc_nvenc,libx265. |
| `--audio-encoder <audio-encoder>` | The sound encoder: aac, libopus, flac, eac3, ac3, libmp3lame, pcm_s24le or pcm_s16le. |
| `--audio-bitrate <audio-bitrate>` | The sound bitrate: 320k. |
| `--channels <channels>` | 1, 2 or 6 channels. A 5.1 sequence exported in stereo is folded down (ITU), per source, before the master limiter. |
| `--loudness <loudness>` | Measure the mix and bring it to this integrated loudness, peaks held under -1 dBFS: -14. |
| `--target-size <target-size>` | Come in under this size: 8MB, in binary units as Discord counts. The picture steps down when it must; the file is checked and encoded again if it is over. |
| `--pixel-format <pixel-format>` | The pixel format: yuv420p10le for ten bits, yuv422p10le for 4:2:2 ten bit. Always BT.709. |
| `--audio-only` | Write the sound alone, in a sound file for the preset's encoder: AAC in .m4a, Opus in .opus, FLAC, WAV or MP3. |
| `--start <start>` | Export from here, in sequence time. |
| `--end <end>` | Export to here, in sequence time. |

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

### `jazz export batch <project> <folder>`

Queue an export for each range marker or each stretch.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<folder>` | The folder the files go in. |

| Option | Meaning |
|---|---|
| `--preset <preset>` | Which preset. Default: youtube-1080p. |
| `--markers` | Export each range marker. |
| `--ranges <ranges>` | Export each of these stretches: 00:10-00:20,01:00-01:30. |
| `--name-contains <name-contains>` | Only markers whose name contains this. |
| `--mode <auto|copy|encode|smart>` | auto, smart, copy, or encode. Default: auto. |
| `--sequence <id>` | Which sequence. |
| `--priority <low|normal|high>` | low, normal or high. Default: normal. |
| `--no-save` | Do not write the project back. |

### `jazz export cancel <project> <job-id>`

Stop a queued or running export.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<job-id>` | The job id. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz export clear <project>`

Remove finished jobs from the export queue.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz export contact-sheet <project> <output>`

Write a contact sheet of frames across a sequence.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<output>` | The .png file to write. |

| Option | Meaning |
|---|---|
| `--columns <n>` | Tiles across. Default: 4. |
| `--rows <n>` | Tiles down. Default: 4. |
| `--width <n>` | The sheet's width in pixels. Default: 1920. |
| `--sequence <id>` | Which sequence. |
| `--start <time>` | From here. |
| `--end <time>` | To here. |
| `--times <times>` | Fractions of the way through, 0 to 1, rather than even steps. |
| `--clip <id>` | Through this clip rather than the sequence. |
| `--no-save` | Do not write the project back. |

### `jazz export enqueue <project> <output>`

Queue an export of a sequence to a file.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<output>` | Where to write the file. |

| Option | Meaning |
|---|---|
| `--preset <preset>` | Which preset; jazz presets list shows them. Default: youtube-1080p. |
| `--mode <auto|copy|encode|smart>` | auto, smart, copy, or encode. Default: auto. |
| `--sequence <id>` | Which sequence. |
| `--snap-to-keyframes` | For a copy, move cuts to the nearest keyframe. |
| `--use-in-out` | Export only between the in and out points. |
| `--use-external-ffmpeg` | Encode through ffmpeg.exe. |
| `--subtitles <soft|burn|sidecar|none>` | soft, burn, sidecar or none. Default: soft. |
| `--sidecar-format <srt|vtt|ass>` | srt, vtt or ass. Default: srt. |
| `--chapters` | Write chapter marks into the file. Default: true. |
| `--size <size>` | Fit the picture inside this size, for example 1280x720. |
| `--fps <rate>` | Write at this frame rate. |
| `--quality <n>` | Constant quality: CRF or CQ, lower is better. |
| `--bitrate <bitrate>` | A picture bitrate instead of constant quality: 8M, 2500k. |
| `--encoder <list>` | The encoders to try, in order, comma separated. |
| `--audio-encoder <audio-encoder>` | The sound encoder: aac, libopus, flac, eac3. |
| `--audio-bitrate <audio-bitrate>` | The sound bitrate: 320k. |
| `--channels <n>` | 1, 2 or 6 channels. |
| `--loudness <number>` | Normalise the mix to this many LUFS, for example -14. |
| `--target-size <target-size>` | Come in under this size: 8MB. |
| `--pixel-format <pixel-format>` | yuv420p10le for ten bits, yuv422p10le for 4:2:2 ten bit. |
| `--audio-only` | Write the sound alone, in a sound file for its encoder: .m4a, .opus, .flac, .wav or .mp3. |
| `--start <time>` | Export from here. |
| `--end <time>` | Export to here. |
| `--priority <low|normal|high>` | low, normal or high. Default: normal. |
| `--open-folder` | Show the file in Explorer when it is done. |
| `--run <run>` | A script to run when it is done, given the file's path. |
| `--id <id>` | The id for the new job. |
| `--no-save` | Do not write the project back. |

### `jazz export list <project>`

List export jobs and their progress.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz export log <project> <job-id>`

Show what an export has done, step by step.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<job-id>` | The job id. |

### `jazz export pause <project> [job-id]`

Hold an export, or all of them.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<job-id>` | The job id; every job when left out. Optional. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz export plan <project> <output>`

Plan an export without running it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<output>` | Where the file would go. |

| Option | Meaning |
|---|---|
| `--preset <preset>` | Which preset. Default: youtube-1080p. |
| `--mode <auto|copy|encode|smart>` | auto, smart, copy, or encode. Default: auto. |
| `--sequence <id>` | Which sequence. |
| `--snap-to-keyframes` | For a copy, move cuts to the nearest keyframe. |
| `--use-in-out` | Export only between the in and out points. |
| `--use-external-ffmpeg` | Encode through ffmpeg.exe. |
| `--subtitles <soft|burn|sidecar|none>` | soft, burn, sidecar or none. Default: soft. |
| `--sidecar-format <srt|vtt|ass>` | srt, vtt or ass. Default: srt. |
| `--chapters` | Write chapter marks into the file. Default: true. |
| `--size <size>` | Fit the picture inside this size, for example 1280x720. |
| `--fps <rate>` | Write at this frame rate. |
| `--quality <n>` | Constant quality: CRF or CQ, lower is better. |
| `--bitrate <bitrate>` | A picture bitrate instead of constant quality: 8M, 2500k. |
| `--encoder <list>` | The encoders to try, in order, comma separated. |
| `--audio-encoder <audio-encoder>` | The sound encoder: aac, libopus, flac, eac3. |
| `--audio-bitrate <audio-bitrate>` | The sound bitrate: 320k. |
| `--channels <n>` | 1, 2 or 6 channels. |
| `--loudness <number>` | Normalise the mix to this many LUFS, for example -14. |
| `--target-size <target-size>` | Come in under this size: 8MB. |
| `--pixel-format <pixel-format>` | yuv420p10le for ten bits, yuv422p10le for 4:2:2 ten bit. |
| `--audio-only` | Write the sound alone, in a sound file for its encoder: .m4a, .opus, .flac, .wav or .mp3. |
| `--start <time>` | Export from here. |
| `--end <time>` | Export to here. |

### `jazz export resume <project> [job-id]`

Let a paused export run again, or all of them.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<job-id>` | The job id; every paused job when left out. Optional. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz export set-priority <project> <job-id> <priority>`

Move an export up or down the queue.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<job-id>` | The job id. |
| `<priority>` | low, normal or high. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz export still <project> <output>`

Write the frame at a time as a PNG.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<output>` | The .png file to write. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The sequence time to draw. |
| `--sequence <id>` | Which sequence. |
| `--size <size>` | Fit the picture inside this size, for example 1280x720. |
| `--no-save` | Do not write the project back. |

## `jazz fonts`

The font families titles can use.

### `jazz fonts list`

List the font families titles can use: installed, and with --project the project's own.

| Option | Meaning |
|---|---|
| `--project <project>` | A .jazz file to read alongside, for its fonts folder and relative paths. |
| `--search <search>` | Only families whose name contains this. |

## `jazz history`

The session's undo history.

### `jazz history list <project>`

List what has been done, oldest first.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--limit <n>` | How many entries to return. Default: 50. |

## `jazz ids`

Identifiers.

### `jazz ids new`

Print fresh ULIDs for hand-editing a project file.

| Option | Meaning |
|---|---|
| `--count <count>`, `-n <count>` | How many identifiers to print. Default: 1. |

Prints fresh ULIDs, for hand-editing a project file.

```bash
jazz ids new -n 3
```


With `--json`: `{"ids": [...]}`.

## `jazz keyframe`

Keyframes on any animatable parameter: add, move, set value, interpolation and handles, remove.

### `jazz keyframe add <project> <owner-id> <param>`

Add a keyframe to a parameter.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. When, on the sequence. |
| `--value <value>` | The value; what it is worth there when not given. |
| `--interp <hold|linear|bezier|ease-in|ease-out|ease-in-out>` | hold, linear, bezier, ease-in, ease-out or ease-in-out. |
| `--local` | Read --at from the clip's start rather than the sequence's. |
| `--no-save` | Do not write the project back. |

### `jazz keyframe move <project> <owner-id> <param>`

Move a keyframe to another time.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The keyframe's time on the sequence. |
| `--to <time>` | Required. Its new time on the sequence. |
| `--local` | Read the times from the clip's start rather than the sequence's. |
| `--no-save` | Do not write the project back. |

### `jazz keyframe remove <project> <owner-id> <param>`

Remove a keyframe from a parameter.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The keyframe's time on the sequence. |
| `--local` | Read --at from the clip's start rather than the sequence's. |
| `--no-save` | Do not write the project back. |

### `jazz keyframe set-handles <project> <owner-id> <param>`

Set a keyframe's bezier handles.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The keyframe's time on the sequence. |
| `--in <in>` | The handle arriving, as 'time, value' from 0 to 1. |
| `--out <out>` | The handle leaving, as 'time, value' from 0 to 1. |
| `--local` | Read --at from the clip's start rather than the sequence's. |
| `--no-save` | Do not write the project back. |

### `jazz keyframe set-interp <project> <owner-id> <param>`

Change how the curve leaves a keyframe.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The keyframe's time on the sequence. |
| `--interp <hold|linear|bezier|ease-in|ease-out|ease-in-out>` | Required. hold, linear, bezier, ease-in, ease-out or ease-in-out. |
| `--local` | Read --at from the clip's start rather than the sequence's. |
| `--no-save` | Do not write the project back. |

### `jazz keyframe set-value <project> <owner-id> <param>`

Change a keyframe's value.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The keyframe's time on the sequence. |
| `--value <value>` | Required. The new value. |
| `--local` | Read --at from the clip's start rather than the sequence's. |
| `--no-save` | Do not write the project back. |

## `jazz layout`

Commands and queries about layout.

### `jazz layout apply <project> <layout>`

Lay clips out on the frame: facecam corner, side by side, before and after, grids of 2, 3 and 4.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<layout>` | facecam, side-by-side, before-after, grid-2, grid-3 or grid-4. |

| Option | Meaning |
|---|---|
| `--clips <list>` | Required. The clips, in the order the layout fills. |
| `--corner <corner>` | facecam: top-left, top-right, bottom-left or bottom-right. Default: bottom-right. Default: bottom-right. |
| `--size <number>` | facecam: its width as a fraction of the frame. Default: 0.28. Default: 0.28. |
| `--margin <number>` | facecam: distance from the edges, in pixels. Default: 40. Default: 40. |
| `--split <number>` | before-after: where the second clip starts, 0 to 1. Default: 0.5. Default: 0.5. |
| `--gap <number>` | Pixels between cells. Default: 0. Default: 0. |
| `--no-save` | Do not write the project back. |

### `jazz layout list <project>`

The layouts layout.apply knows, with how many clips each takes.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

## `jazz marker`

Markers on a sequence or a clip: points and ranges, names, colours, chapters.

### `jazz marker add <project> [name]`

Put a marker on a sequence or a clip.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<name>` | Its label. Optional. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. Where it sits. |
| `--dur <time>` | Non-zero for a range marker. |
| `--color <color>` | A hex colour or a name such as red. |
| `--note <note>` | Longer text, shown on hover. |
| `--chapter` | Export this marker as a chapter. |
| `--clip <id>` | Put it on this clip instead. |
| `--sequence <id>` | Which sequence. |
| `--id <id>` | The identifier to give it. |
| `--no-save` | Do not write the project back. |

### `jazz marker list <project>`

List markers, in time order.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--clip <id>` | Only the markers on this clip. |
| `--sequence <id>` | Which sequence. |
| `--chapters` | Only chapter markers. |

### `jazz marker remove <project> <marker-id>`

Remove a marker.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<marker-id>` | The marker id. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz marker set <project> <marker-id>`

Change a marker.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<marker-id>` | The marker id. |

| Option | Meaning |
|---|---|
| `--name <name>` | Its label. |
| `--at <time>` | Where it sits. |
| `--dur <time>` | Non-zero for a range marker. |
| `--color <color>` | A hex colour or a name such as red. |
| `--note <note>` | Longer text, shown on hover. |
| `--chapter` | Export this marker as a chapter. |
| `--no-save` | Do not write the project back. |

## `jazz mask`

Masks on clips and effects: shapes, paths, feather, expansion, how they combine.

### `jazz mask add <project> <owner-id>`

Limit a clip's picture, or one of its effects, to a shape.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip or effect id. |

| Option | Meaning |
|---|---|
| `--shape <rectangle|ellipse|polygon|bezier>` | Required. rectangle, ellipse, polygon or bezier. |
| `--x <number>` | Left edge of the bounds, source pixels. |
| `--y <number>` | Top edge of the bounds. |
| `--width <number>` | Width of the bounds. |
| `--height <number>` | Height of the bounds. |
| `--path <path>` | SVG path data for a polygon or bezier. |
| `--feather <number>` | Edge softening in pixels. Default: 0. |
| `--opacity <number>` | 0 to 1. Default: 1. |
| `--mode <add|subtract|intersect>` | add, subtract or intersect. Default: add. |
| `--invert` | Keep the outside instead. |
| `--expansion <number>` | Grow the shape by this many pixels; negative shrinks it. Default: 0. |
| `--id <id>` | The identifier to give it. |
| `--no-save` | Do not write the project back. |

### `jazz mask remove <project> <mask-id>`

Remove a mask.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<mask-id>` | The mask id. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz mask set <project> <mask-id>`

Change a mask.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<mask-id>` | The mask id. |

| Option | Meaning |
|---|---|
| `--shape <rectangle|ellipse|polygon|bezier>` | rectangle, ellipse, polygon or bezier. |
| `--x <number>` | Left edge of the bounds, source pixels. |
| `--y <number>` | Top edge of the bounds. |
| `--width <number>` | Width of the bounds. |
| `--height <number>` | Height of the bounds. |
| `--path <path>` | SVG path data for a polygon or bezier. |
| `--feather <number>` | Edge softening in pixels. |
| `--opacity <number>` | 0 to 1. |
| `--mode <add|subtract|intersect>` | add, subtract or intersect. |
| `--invert` | Keep the outside instead. |
| `--expansion <number>` | Grow the shape by this many pixels; negative shrinks it. |
| `--enabled` | on or off. |
| `--no-save` | Do not write the project back. |

## `jazz media`

Media files: import, probe, relink, reprobe, set conform options, remove.

### `jazz media add <project> <paths>`

Import files, folders or globs into the project.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<paths>` | Comma-separated files, folders or globs. |

| Option | Meaning |
|---|---|
| `--folder <folder>` | Where they go in the bin. |
| `--tags <list>` | Comma-separated tags. |
| `--color <color>` | A colour label, for example blue. |
| `--conform <fit|fill|stretch|native>` | fit, fill, stretch or native. Default: fit. |
| `--deinterlace <auto|on|off>` | auto, on or off. Default: auto. |
| `--vfr-conform <auto|on|off>` | auto, on or off. Default: auto. |
| `--recursive` | Look inside sub-folders. |
| `--fps <rate>` | The rate an image sequence plays at. |
| `--no-save` | Do not write the project back. |

### `jazz media analyze-motion <project> <media-id>`

Analyse a video's camera motion for stabilization.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<media-id>` | The media id. |

| Option | Meaning |
|---|---|
| `--stream <n>` | The video stream's index in the file. |
| `--no-save` | Do not write the project back. |

### `jazz media check <project>`

Say which media files are there, missing, or changed.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz media get <project> <media-id>`

Describe one media item and its streams.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<media-id>` | The media id. |

### `jazz media list <project>`

List the files the project uses.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--folder <folder>` | Only this bin folder. |
| `--search <search>` | Only items matching this text. |
| `--tag <tag>` | Only items carrying this tag. |

### `jazz media missing <project>`

Find where missing media went, with candidates for each.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--search <list>` | Folders to look in, with their subfolders. |
| `--media <id>` | Only this media item. |

### `jazz media probe <path>`

Read a file and say what is in it, without importing it.

| Argument | Meaning |
|---|---|
| `<path>` | The file to read. |

| Option | Meaning |
|---|---|
| `--project <project>` | A .jazz file to read alongside, for its fonts folder and relative paths. |
| `--conform <fit|fill|stretch|native>` | fit, fill, stretch or native. Default: fit. |
| `--deinterlace <auto|on|off>` | auto, on or off. Default: auto. |
| `--vfr-conform <auto|on|off>` | auto, on or off. Default: auto. |

### `jazz media relink <project> [media-id] [path]`

Point media at files that have moved, by hand or found by hash.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<media-id>` | The media id. Optional. |
| `<path>` | Where the file is now. Optional. |

| Option | Meaning |
|---|---|
| `--force` | Accept a file of a different duration. |
| `--auto` | Find every missing file by hash, then by name and size. |
| `--search <list>` | Folders to look in, with their subfolders. |
| `--no-save` | Do not write the project back. |

### `jazz media remove <project> <media-id>`

Take a file out of the project.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<media-id>` | The media id. |

| Option | Meaning |
|---|---|
| `--with-clips` | Remove the clips that play it too. |
| `--no-save` | Do not write the project back. |

### `jazz media remove-unused <project>`

Remove every media item no clip uses.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--keep-tagged` | Keep items that have tags. |
| `--no-save` | Do not write the project back. |

### `jazz media replace <project> <media-id> <path>`

Swap a media item's file for another, keeping its clips.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<media-id>` | The media id. |
| `<path>` | The new file. |

| Option | Meaning |
|---|---|
| `--force` | Accept a file too short for some clips. |
| `--no-save` | Do not write the project back. |

### `jazz media reprobe <project> [media-id]`

Read a media file again and update what the project knows.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<media-id>` | The media id, or every one when left out. Optional. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz media set <project> <media-id>`

Change a media item's name, folder, tags or conform settings.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<media-id>` | The media id. |

| Option | Meaning |
|---|---|
| `--name <name>` | Its display name. |
| `--folder <folder>` | Where it sits in the bin. |
| `--tags <list>` | Comma-separated tags, replacing what was there. |
| `--color <color>` | A colour label. |
| `--conform <fit|fill|stretch|native>` | fit, fill, stretch or native. |
| `--deinterlace <auto|on|off>` | auto, on or off. |
| `--vfr-conform <auto|on|off>` | auto, on or off. |
| `--no-save` | Do not write the project back. |

### `jazz media unwatch <project> [folder]`

Stop watching a folder.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<folder>` | The folder, or every one when left out. Optional. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz media usage <project>`

Say which clips and sequences use each media item.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--media <id>` | Only this media item. |
| `--unused` | Only items no clip uses. |

### `jazz media watch <project> <folder>`

Watch a folder and bring in new recordings as they finish.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<folder>` | The folder to watch. |

| Option | Meaning |
|---|---|
| `--tags <list>` | Tags for everything it brings in. |
| `--bin <bin>` | The bin folder they go in. |
| `--existing` | Bring in what is already there too. |
| `--no-save` | Do not write the project back. |

### `jazz media watches <project>`

List the folders being watched for new recordings.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

## `jazz param`

Any parameter by its address, on a clip, a track, an effect or a mask.

### `jazz param clear-driver <project> <owner-id> <param>`

Stop driving a parameter, back to its keyframes or value.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz param clear-keyframes <project> <owner-id> <param>`

Remove every keyframe from a parameter.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name. |

| Option | Meaning |
|---|---|
| `--at <time>` | Keep the value from this time; the first keyframe's when not given. |
| `--local` | Read --at from the clip's start rather than the sequence's. |
| `--no-save` | Do not write the project back. |

### `jazz param get <project> <owner-id> <param>`

Show one parameter, and its value at a time.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name. |

| Option | Meaning |
|---|---|
| `--at <time>` | Also say what it is worth at this time on the sequence. |

### `jazz param list <project> <owner-id>`

List the parameters of a clip, track, effect or mask.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |

### `jazz param set <project> <owner-id> <param> <value>`

Set a parameter of a clip, track, effect or mask.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name, for example transform.position or radius. |
| `<value>` | The value, for example 12, '100, 50' or #FF8800. |

| Option | Meaning |
|---|---|
| `--at <time>` | For a keyframed parameter, the keyframe's time on the sequence. |
| `--local` | Read --at from the clip's start rather than the sequence's. |
| `--no-save` | Do not write the project back. |

### `jazz param set-driver <project> <owner-id> <param> <expression>`

Drive a parameter with an expression (time, wiggle, audio, param, marker) instead of keyframes.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<owner-id>` | The clip, track, effect or mask id. |
| `<param>` | The parameter name. |
| `<expression>` | The expression, for example value + wiggle(2, 12). |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

## `jazz perf`

Measure the engine. Numbers land in Docs/PERF.md.

### `jazz perf decode <file>`

Decode a file as fast as possible and report throughput.

| Argument | Meaning |
|---|---|
| `<file>` | The media file to decode. |

| Option | Meaning |
|---|---|
| `--software` | Force the software decoder instead of trying D3D11VA. |
| `--passes <passes>` | Decode the file this many times. More than one checks for leaks. Default: 1. |
| `--reuse` | Keep one decoder across passes and rewind, the way playback loops do. This is what separates a decode leak from the cost of churning decoders. |

Decodes a file as fast as the machine allows and reports throughput, managed allocation per
frame, and video memory growth. This is the harness behind spike S2 and the decode half of
`Docs/PERF.md`; it renders nothing, so a regression here is unambiguous.

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
steady-state 96 bytes per frame recorded in `Docs/spikes/S2.md`. The allocation test is the one
that holds the line; this number is for spotting a change, not for quoting.

### `jazz perf scrub <file>`

Seek to random times and report how long a frame takes to reach a texture.

| Argument | Meaning |
|---|---|
| `<file>` | The media file to scrub. |

| Option | Meaning |
|---|---|
| `--software` | Force the software decoder instead of trying D3D11VA. |
| `--requests <requests>` | How many random seeks to make. Default: 200. |
| `--seed <seed>` | The random seed, so a run repeats exactly. Default: 20260923. |
| `--drag` | Move the playhead in small steps, the way a hand does, instead of at random. This is what a scrub actually is; random access is the worst case. |
| `--reverse` | Play backwards one frame at a time, priming each group of pictures. Reports how many frames missed the playback budget. |
| `--nearest` | Take the nearest keyframe instead of the exact frame, the way shuttling does. |
| `--proxy` | Scrub the file's half size proxy, making it first (in the cache) if there is none. |

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
person does; nearest is what a shuttle does. `Docs/PERF.md` records all of them for the corpus.

### `jazz perf audio <file>`

Play a many-clip project through the sound card and count the gaps.

| Argument | Meaning |
|---|---|
| `<file>` | A media file with audio; each stream becomes a track. |

| Option | Meaning |
|---|---|
| `--clips <clips>` | How many clips to cut across the tracks. Default: 30. |
| `--minutes <minutes>` | How long to play. Default: 10. |
| `--device <device>` | A playback device id. The default device when left out. |
| `--audible` | Play at full volume. Silent by default: the mix and the device do the same work, and nobody has to listen to test tones. |

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

### `jazz perf playback <file>`

Play a long sequence through the playback engine and count dropped frames.

| Argument | Meaning |
|---|---|
| `<file>` | A media file with a picture; it is laid end to end to fill the run. |

| Option | Meaning |
|---|---|
| `--minutes <minutes>` | How long to play. Default: 5. |
| `--software` | Decode on the CPU, as CI and a machine without a video decoder do. |
| `--audible` | Play at full volume. Silent by default: the clock is the sound card's either way. |
| `--panel <panel>` | The size of the preview surface each frame is drawn into. Default: 2560x1440. |
| `--layers <layers>` | Video tracks to stack, each scaled and turned into its own quadrant, on its own decoder. Default: 1. |
| `--effects <effects>` | Picture effects stacked on every clip at their defaults, by type id, comma separated: video.blur.gaussian,video.glow. An id that is not a picture effect is refused. |
| `--vfx` | Over the file, two moving layers with motion blur (a title and a shape), a particle layer, and a heavy hit every four seconds: Phase 29a's bar. |

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

### `jazz perf thumbs <file>`

Time a clip's thumbnails and waveform from a cold cache: visible, whole strip, and sound.

| Argument | Meaning |
|---|---|
| `<file>` | A media file with a picture. |

| Option | Meaning |
|---|---|
| `--zoom <zoom>` | Pixels per second to measure at, repeatable. The fit zoom and 40 when left out. |

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

### `jazz perf project <file>`

Time a big project: load, open, and the edits a person makes, each to the change being published.

| Argument | Meaning |
|---|---|
| `<file>` | A media file with a picture and sound, cut into the project's clips. |

| Option | Meaning |
|---|---|
| `--clips <clips>` | How many clips, over four picture and four sound tracks. Default: 2000. |
| `--edits <edits>` | Rounds of edits: a move, a trim and a split on a random clip, each undone. Default: 100. |

### `jazz perf soak <file>`

Play and edit for hours and watch memory, video memory and handles: steady, or growing.

| Argument | Meaning |
|---|---|
| `<file>` | A media file with a picture, laid end to end on two layers and looped. |

| Option | Meaning |
|---|---|
| `--hours <hours>` | How long to play and edit. Default: 8. |
| `--csv <csv>` | Where to write a sample a minute as it is taken, so a run cut short still says something. |

## `jazz playback`

The transport of a running editor: play, pause, seek, loop, rate.

### `jazz playback clear-in-out <project>`

Remove the in and out points.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz playback go-to <project> <target>`

Send the playhead to the start, the end, an edit or a marker.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<target>` | start, end, next-edit, prev-edit, next-marker, prev-marker, in or out. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz playback loop <project> [on]`

Loop playback over the in and out points.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<on>` | on or off; leave it out to flip it. Optional. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz playback pause <project>`

Pause where the playhead is.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz playback play <project>`

Play from the playhead.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz playback seek <project> <to>`

Move the playhead.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<to>` | Where to put the playhead. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz playback set-in <project>`

Set the in point.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--at <time>` | Where; defaults to the playhead. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz playback set-out <project>`

Set the out point.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--at <time>` | The last frame inside; defaults to the playhead. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz playback set-quality <project> <quality>`

Choose the preview resolution.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<quality>` | full, half, quarter or auto. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz playback shuttle <project>`

Play at a rate, forwards or backwards.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--rate <number>` | Required. 2 is double speed, -1 is backwards. |
| `--no-save` | Do not write the project back. |

### `jazz playback state <project>`

Where the playhead is and what playback is doing.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz playback step <project>`

Step the playhead by whole frames.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--frames <n>` | How many frames; negative steps back. Default: 1. |
| `--no-save` | Do not write the project back. |

### `jazz playback stop <project>`

Stop and return to where playback started.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz playback toggle <project>`

Play when paused, pause when playing.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

## `jazz presets`

Export presets, built in and your own. Need no project.

### `jazz presets delete <name>`

Delete one of your export presets.

| Argument | Meaning |
|---|---|
| `<name>` | The preset. |

| Option | Meaning |
|---|---|
| `--project <project>` | A .jazz file to read alongside, for its fonts folder and relative paths. |

### `jazz presets get <name>`

Show an export preset in full.

| Argument | Meaning |
|---|---|
| `<name>` | The preset. |

| Option | Meaning |
|---|---|
| `--project <project>` | A .jazz file to read alongside, for its fonts folder and relative paths. |

### `jazz presets list`

List the export presets, built in and your own.

| Option | Meaning |
|---|---|
| `--project <project>` | A .jazz file to read alongside, for its fonts folder and relative paths. |
| `--category <category>` | Only this category: youtube, discord, device, archive, web, image, audio or other. |

### `jazz presets save <name>`

Save an export preset of your own.

| Argument | Meaning |
|---|---|
| `<name>` | What to call it. |

| Option | Meaning |
|---|---|
| `--project <project>` | A .jazz file to read alongside, for its fonts folder and relative paths. |
| `--from <from>` | The preset to start from. |
| `--json <json>` | A whole preset as JSON, or a .json file holding one. |
| `--label <label>` | What the export dialog calls it. |
| `--description <description>` | One line on what it is for. |
| `--size <size>` | Fit the picture inside this size, for example 1280x720. |
| `--fps <rate>` | Write at most this frame rate. |
| `--quality <n>` | Constant quality: CRF or CQ, lower is better. |
| `--bitrate <bitrate>` | A picture bitrate instead of constant quality: 8M, 2500k. |
| `--encoder <list>` | The encoders to try, in order, comma separated. |
| `--audio-encoder <audio-encoder>` | The sound encoder: aac, libopus, flac, eac3. |
| `--audio-bitrate <audio-bitrate>` | The sound bitrate: 320k. |
| `--channels <n>` | 1, 2 or 6 channels. |
| `--loudness <number>` | Normalise the mix to this many LUFS, for example -14. |
| `--target-size <target-size>` | Come in under this size: 8MB. |
| `--pixel-format <pixel-format>` | yuv420p10le for ten bits, yuv422p10le for 4:2:2 ten bit. |
| `--audio-only` | Write the sound alone, in a sound file for its encoder: .m4a, .opus, .flac, .wav or .mp3. |

## `jazz project`

The project: settings, default tone mapping, a summary.

### `jazz project archive <project> <to>`

Write the project and its media into one zip.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<to>` | The zip file to write. |

| Option | Meaning |
|---|---|
| `--trim` | Keep only the parts clips use, with handles. |
| `--handles <time>` | How much to keep either side of each used part (default 1s). |
| `--no-save` | Do not write the project back. |

### `jazz project consolidate <project> <to>`

Gather the project and its media into one folder.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<to>` | The folder to gather into. |

| Option | Meaning |
|---|---|
| `--trim` | Keep only the parts clips use, with handles. |
| `--handles <time>` | How much to keep either side of each used part (default 1s). |
| `--move` | Move the files instead of copying them. |
| `--overwrite` | Write into a folder that already has a project in it. |
| `--no-save` | Do not write the project back. |

### `jazz project get <project>`

Summarise the project: settings, counts and duration.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz project set-settings <project>`

Change the project frame rate, size or audio format.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--fps <rate>` | Frame rate, for example 30 or 30000/1001. |
| `--size <size>` | Frame size, for example 1920x1080 or 4k. |
| `--sample-rate <n>` | Audio sample rate. |
| `--channels <n>` | Audio channel count. |
| `--color-space <color-space>` | Working colour space. |
| `--no-save` | Do not write the project back. |

### `jazz project set-tone-map <project>`

Set how HDR clips are brought down to SDR by default.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--operator <bt2390|hable|mobius|clip>` | bt2390, hable, mobius or clip. |
| `--desaturate <number>` | How much compressed highlights lose their colour, 0 to 1. |
| `--no-save` | Do not write the project back. |

## `jazz proxy`

Proxies: half size copies of heavy media for smooth editing.

### `jazz proxy generate <project>`

Make proxy files for smooth editing of heavy footage.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--media <id>` | The media id. |
| `--all` | Every movie in the project. |
| `--auto` | Every movie that is 4K, AV1 or 10-bit HEVC. |
| `--scale <number>` | Size against the source: 0.5 or 0.25. Default: 0.5. |
| `--preset <preset>` | proxy-h264-intra or proxy-dnxhr-lb. Default: proxy-h264-intra. |
| `--no-save` | Do not write the project back. |

### `jazz proxy list <project>`

List each movie's proxy and whether one is suggested.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz proxy remove <project>`

Delete proxy files.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--media <id>` | The media id. |
| `--all` | Every media item in the project. |
| `--no-save` | Do not write the project back. |

### `jazz proxy set-enabled <project> <enabled>`

Play proxies instead of their sources, or stop.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<enabled>` | true to play proxies, false for the sources. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

## `jazz recovery`

Unsaved work after a crash: check what there is, bring it back by replaying every command since the last save, or set it aside.

### `jazz recovery accept <project>`

Bring back unsaved work after a crash: the saved project with every command since replayed.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--file <file>` | A rescued untitled project from recovery.check, instead of this project's own. |
| `--no-save` | Do not write the project back. |

### `jazz recovery check <project>`

Say whether there is unsaved work to recover beside the project, and how much.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz recovery discard <project>`

Decline the recovery: set the autosave copy and command history aside and keep the project as saved.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--file <file>` | A rescued untitled project to remove instead. |
| `--no-save` | Do not write the project back. |

## `jazz rpc`

Talk JSON-RPC to a running editor or 'jazz serve': call a method, list them, watch events, or pass stdin through.

### `jazz rpc call <method> [params]`

Call one method and print the result as JSON.

| Argument | Meaning |
|---|---|
| `<method>` | The method: a registry name (clip.split) or a session method (session.info). |
| `<params>` | Its params as a JSON object: '{"clipId": "...", "at": "2s"}'. None when left out. Optional. |

### `jazz rpc list`

List every method: the session methods and every registry command and query, with their params. Needs no editor.

### `jazz rpc events`

Print the editor's events as they happen, one JSON object a line, until Ctrl+C.

| Option | Meaning |
|---|---|
| `--events <events>` | Which events, comma separated: project.changed, command.completed, session.opened, selection.changed, playhead.moved, export.progress, export.done, log. All of them by default. Default: *. |

### `jazz rpc stdio`

Pass JSON-RPC lines from stdin to the editor and its lines to stdout, as they are: for embedding jazz in another program.

### `jazz rpc instances`

List the running editors and 'jazz serve' processes, and how to reach them.

## `jazz scopes`

Waveform, vectorscope and histogram measurements of the picture.

### `jazz scopes measure <project>`

Measure a frame's histograms and clipping.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. When, on the timeline. |
| `--sequence <id>` | Which sequence. |

## `jazz selection`

What is selected in a running editor.

### `jazz selection clear <project>`

Select nothing.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz selection get <project>`

Which clips and markers are selected.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz selection set <project> <ids>`

Choose which clips and markers are selected.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<ids>` | Comma-separated clip and marker ids. |

| Option | Meaning |
|---|---|
| `--mode <replace|add|remove|toggle>` | replace, add, remove or toggle. Default: replace. |
| `--no-save` | Do not write the project back. |

## `jazz sequence`

Sequences: create, rename, set active, their own settings.

### `jazz sequence create <project> <name>`

Add an empty sequence.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<name>` | The sequence name. |

| Option | Meaning |
|---|---|
| `--set-active` | Show it in the editor. Default: true. |
| `--id <id>` | The identifier to give it. |
| `--no-save` | Do not write the project back. |

### `jazz sequence list <project>`

List the sequences in the project.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz sequence reframe <project>`

Make a vertical version of a sequence (for Shorts, Reels, TikTok): a cropped or fitted picture over a blurred copy, the crop panned or following a tracked point.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--size <size>` | The new frame. Default: 1080x1920. |
| `--from <id>` | The sequence to reframe; the active one when not given. |
| `--mode <crop|fit>` | crop or fit. Default: crop. Default: crop. |
| `--window <number>` | crop: the window's width over its height. Default: 0.8. Default: 0.8. |
| `--follow <follow>` | crop: a point track to keep in the middle. |
| `--blur <number>` | The background's blur in pixels. Default: 40. Default: 40. |
| `--name <name>` | What to call the new sequence. |
| `--no-save` | Do not write the project back. |

### `jazz sequence remove <project> <sequence-id>`

Remove a sequence.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<sequence-id>` | The sequence id. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz sequence rename <project> <sequence-id> <name>`

Rename a sequence.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<sequence-id>` | The sequence id. |
| `<name>` | The new name. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz sequence set-active <project> <sequence-id>`

Choose the sequence the editor shows.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<sequence-id>` | The sequence id. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz sequence set-motion-blur <project>`

Set motion blur for every animated layer in a sequence.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--angle <number>` | Shutter angle in degrees: 180 is half a frame. Default: 180. |
| `--samples <n>` | Moments averaged, 2 to 64. Default: 32. |
| `--off` | Turn it off. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz sequence set-settings <project> <sequence-id>`

Give a sequence its own frame rate, size or audio format.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<sequence-id>` | The sequence id. |

| Option | Meaning |
|---|---|
| `--fps <rate>` | Frame rate, for example 30 or 30000/1001. |
| `--size <size>` | Frame size, for example 1920x1080 or 4k. |
| `--sample-rate <n>` | Audio sample rate. |
| `--channels <n>` | Audio channel count. |
| `--color-space <color-space>` | Working colour space. |
| `--inherit` | Drop the override and follow the project. |
| `--no-save` | Do not write the project back. |

## `jazz settings`

Commands and queries about settings.

### `jazz settings get <project>`

List the editor's settings with their values and defaults.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--section <section>` | Only this section. |

### `jazz settings set <project> <key> <value>`

Change one of the editor's settings.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<key>` | The setting, as section.name (see settings.get). |
| `<value>` | Its new value: JSON, or plain text. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

## `jazz subtitle`

Subtitles: import and export SRT, VTT and ASS; cues, their text, times and place; styles.

### `jazz subtitle add <project> <track-id>`

Add a subtitle cue.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The subtitle track id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. When it appears. |
| `--dur <time>` | Required. How long it stays. |
| `--text <text>` | Required. What it says, as markup. |
| `--align <bottom-left|bottom|bottom-right|left|middle|right|top-left|top|top-right>` | bottom, top, middle, bottom-left and so on. Default: bottom. |
| `--id <id>` | The identifier to give it. |
| `--no-save` | Do not write the project back. |

### `jazz subtitle export <project> <track-id>`

Write a subtitle track as SRT, VTT or ASS.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The subtitle track id. |

| Option | Meaning |
|---|---|
| `--format <srt|vtt|ass>` | srt, vtt or ass. |
| `--out <out>` | The file to write. |

### `jazz subtitle import <project> [file]`

Import subtitles from a file or an embedded stream.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<file>` | A .srt, .vtt, .ass or .ssa file. Optional. |

| Option | Meaning |
|---|---|
| `--media <id>` | Or a media item with subtitle streams. |
| `--stream <n>` | Which of its streams. |
| `--track <id>` | A subtitle track to add to. |
| `--language <language>` | The language, such as eng. |
| `--offset <time>` | Move every cue by this much. |
| `--sequence <id>` | Which sequence. |
| `--id <id>` | The identifier for a new track. |
| `--no-save` | Do not write the project back. |

### `jazz subtitle list <project> <track-id>`

List a subtitle track's cues.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The subtitle track id. |

### `jazz subtitle read <project> [file]`

Show what a subtitle file or stream holds.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<file>` | A .srt, .vtt, .ass or .ssa file. Optional. |

| Option | Meaning |
|---|---|
| `--media <id>` | Or a media item with subtitle streams. |
| `--stream <n>` | Which of its streams. |

### `jazz subtitle replace <project> <track-id>`

Find and replace text in a subtitle track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The subtitle track id. |

| Option | Meaning |
|---|---|
| `--find <find>` | Required. What to look for. |
| `--with <with>` | What to put there. |
| `--match-case` | Match capitals exactly. |
| `--no-save` | Do not write the project back. |

### `jazz subtitle set-align <project> <cue-id> <align>`

Move a subtitle cue to another place on the frame.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<cue-id>` | The cue id. |
| `<align>` | bottom, top, middle, bottom-left and so on. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz subtitle set-style <project> <track-id>`

Change how a subtitle track's cues look.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The subtitle track id. |

| Option | Meaning |
|---|---|
| `--font <font>` | The font family. |
| `--weight <weight>` | regular, semibold, bold and so on. |
| `--italic` | Slanted. |
| `--size <number>` | Text height as a fraction of the frame, such as 0.045. |
| `--color <color>` | The letters, sRGB hex. |
| `--outline <outline>` | The outline's colour. |
| `--outline-width <number>` | The outline's width as a fraction of the frame. |
| `--box <box>` | A box behind each cue, hex with alpha. |
| `--shadow <shadow>` | A soft shadow, hex with alpha. |
| `--margin <number>` | Distance from the edge as a fraction of the frame. |
| `--max-lines <n>` | The most lines a cue. |
| `--max-chars <n>` | The most characters a line. |
| `--no-save` | Do not write the project back. |

### `jazz subtitle set-text <project> <cue-id>`

Change what a subtitle cue says.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<cue-id>` | The cue id. |

| Option | Meaning |
|---|---|
| `--text <text>` | Required. What it says, as markup. |
| `--no-save` | Do not write the project back. |

### `jazz subtitle set-time <project> <cue-id>`

Change when a subtitle cue appears or goes.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<cue-id>` | The cue id. |

| Option | Meaning |
|---|---|
| `--at <time>` | When it appears. |
| `--dur <time>` | How long it stays. |
| `--end <time>` | When it goes. |
| `--no-save` | Do not write the project back. |

### `jazz subtitle shift <project> <track-id>`

Move a subtitle track's cues earlier or later.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The subtitle track id. |

| Option | Meaning |
|---|---|
| `--by <time>` | Required. How far; negative is earlier. |
| `--from <time>` | Only cues from this time on. |
| `--no-save` | Do not write the project back. |

### `jazz subtitle split-long <project> <track-id>`

Break long subtitle cues into lines and shorter cues.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The subtitle track id. |

| Option | Meaning |
|---|---|
| `--max-chars <n>` | The most characters a line. |
| `--max-lines <n>` | The most lines a cue. |
| `--no-save` | Do not write the project back. |

## `jazz template`

Commands and queries about template.

### `jazz template apply <project> <name>`

Place a motion template (hero intro, feature callout, end card, countdown, coming soon, wishlist pop) with its parameters filled in: the way to build a trailer from a brief.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<name>` | The template; template.list names them. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The moment on the sequence it is placed. |
| `--param <list>` | Values as name=value, several separated by commas: text=Out now,accent=#FF8800. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz template list <project>`

The motion templates, their parameters and what each makes.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz template save-selection <project> <name>`

Save clips and their effects as a motion template, with chosen values as parameters.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<name>` | What to call it, in kebab case. |

| Option | Meaning |
|---|---|
| `--clips <list>` | The clips; the selection when not given. |
| `--promote <list>` | Parameter names that become the template's parameters. |
| `--label <label>` | What the editor calls it. |
| `--description <description>` | What it makes. |
| `--force` | Replace a template of the same name. |
| `--no-save` | Do not write the project back. |

## `jazz timeline`

The timeline as a whole: describe it, magnetic mode. In and out points are in playback.

### `jazz timeline describe <project>`

Describe a sequence in readable text.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--sequence <id>` | Which sequence. |
| `--detail <brief|full>` | brief or full. Default: brief. |

### `jazz timeline set-magnetic <project> <magnetic>`

Keep the primary picture track gapless.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<magnetic>` | true to turn it on. |

| Option | Meaning |
|---|---|
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

## `jazz title`

Titles: add from presets, set text, font, look and animation, measure.

### `jazz title add <project>`

Put a title on the timeline from a preset.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. Where it starts on the timeline. |
| `--text <text>` | The text, as markup: [b]bold[/b], [color=#FFCC00]gold[/color], \n for a new line. |
| `--preset <preset>` | Which preset, as title list-presets shows them; title-card when left out. |
| `--dur <time>` | How long it lasts; the preset's when left out. |
| `--track <id>` | Which video track; the highest free one when left out. |
| `--font <font>` | The font family, as fonts list shows them. |
| `--size <size>` | Text height in sequence pixels. |
| `--color <color>` | The fill colour, for example #FFFFFF. |
| `--align <align>` | left, centre or right. |
| `--box <box>` | The box behind the text as a colour, for example #00000099; #00000000 for none. |
| `--shadow <shadow>` | The shadow as a colour, for example #000000A0; #00000000 for none. |
| `--stroke <stroke>` | The outline: a width in pixels, then optionally a colour, as '4' or '4 #000000'. |
| `--anim-in <anim-in>` | What it comes in with: none, fade, slide-left, typewriter, word-reveal and more. |
| `--anim-out <anim-out>` | What it goes out with, from the same list. |
| `--name <name>` | Its display name; its text when left out. |
| `--id <id>` | The identifier to give it. |
| `--sequence <id>` | Which sequence, when no track is named. |
| `--no-save` | Do not write the project back. |

### `jazz title list-presets <project>`

List the title presets and what each sets.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

### `jazz title measure <project> <clip-id>`

Where a title's text sits on the frame, and whether it stays inside title safe.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The title clip id. |

| Option | Meaning |
|---|---|
| `--at <time>` | When, on the sequence; the clip's middle when left out. |

### `jazz title set-animation <project> <clip-id>`

Choose how a title comes in and goes out.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The title clip id. |

| Option | Meaning |
|---|---|
| `--in <in>` | none, fade, slide-left, slide-right, slide-up, slide-down, scale, typewriter, word-reveal, blur or wipe. |
| `--in-dur <time>` | How long the animation in takes. |
| `--out <out>` | The animation out, from the same list. |
| `--out-dur <time>` | How long the animation out takes. |
| `--no-save` | Do not write the project back. |

### `jazz title set-style <project> <clip-id>`

Change a title's font, size, colour, place, box, outline or shadow.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The title clip id. |

| Option | Meaning |
|---|---|
| `--preset <preset>` | Take a preset's look and place first. |
| `--font <font>` | The font family. |
| `--weight <weight>` | thin, extra-light, light, regular, medium, semibold, bold, extra-bold or black. |
| `--italic` | Slanted. |
| `--size <size>` | Text height in sequence pixels. |
| `--color <color>` | The fill colour. |
| `--align <align>` | left, centre or right. |
| `--valign <valign>` | top, middle or bottom. |
| `--position <position>` | Where the text sits from the frame centre, as 'x, y' in sequence pixels. |
| `--width <width>` | The width lines wrap at; 0 for none. |
| `--line-spacing <line-spacing>` | Line height as a multiple of the font's. |
| `--tracking <tracking>` | Extra space between letters, in sequence pixels. |
| `--stroke <stroke>` | The outline: a width in pixels, then optionally a colour, as '4' or '4 #000000'. |
| `--box <box>` | The box's colour; #00000000 for none. |
| `--box-padding <box-padding>` | Space between the text and the box's edge. |
| `--box-radius <box-radius>` | The box's corner radius. |
| `--shadow <shadow>` | The shadow's colour; #00000000 for none. |
| `--shadow-offset <shadow-offset>` | How far the shadow falls, as 'x, y'. |
| `--shadow-blur <shadow-blur>` | How soft the shadow is. |
| `--no-save` | Do not write the project back. |

### `jazz title set-text <project> <clip-id> <text>`

Change what a title says.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The title clip id. |
| `<text>` | The text, as markup: [b]bold[/b], [color=#FFCC00]gold[/color], \n for a new line. |

| Option | Meaning |
|---|---|
| `--plain` | Take the text exactly as typed, with no markup. |
| `--no-save` | Do not write the project back. |

## `jazz track`

Tracks: add, remove, rename, reorder, lock, mute, solo, sync lock, volume and pan.

### `jazz track add <project> <kind>`

Add a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<kind>` | video, audio, subtitle or adjustment. |

| Option | Meaning |
|---|---|
| `--name <name>` | The track name. |
| `--order <n>` | Where it sits in the stack. |
| `--sequence <id>` | Which sequence. |
| `--id <id>` | The identifier to give it. |
| `--no-save` | Do not write the project back. |

### `jazz track list <project>`

List the tracks of a sequence.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--sequence <id>` | Which sequence. |

### `jazz track move <project> <track-id> <to-order>`

Move a track up or down the stack.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |
| `<to-order>` | Where it should end up, counting from the bottom. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz track remove <project> <track-id>`

Remove a track and its clips.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz track rename <project> <track-id> <name>`

Rename a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |
| `<name>` | The new name. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz track set-color <project> <track-id> <color>`

Set a track's colour on the timeline.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |
| `<color>` | #RRGGBB, #RRGGBBAA, or a name such as blue. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz track set-height <project> <track-id> <height>`

Set how tall a track is drawn.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |
| `<height>` | Height in device-independent pixels. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz track set-language <project> <track-id> <language>`

Set the language of what a track says.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |
| `<language>` | An ISO 639-2 code such as eng, or und. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz track set-lock <project> <track-id> <locked>`

Lock or unlock a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |
| `<locked>` | true to lock. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz track set-matte <project> <track-id>`

Show a track only through another track's picture (a track matte).

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |

| Option | Meaning |
|---|---|
| `--source <source>` | The track whose picture is the matte. |
| `--mode <alpha|luma|alpha-inverted|luma-inverted>` | alpha, luma, alpha-inverted or luma-inverted. Default: alpha. Default: alpha. |
| `--off` | Take the track's matte away. |
| `--no-save` | Do not write the project back. |

### `jazz track set-motion-blur <project> <track-id>`

Set motion blur for the animated clips on a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |

| Option | Meaning |
|---|---|
| `--angle <number>` | Shutter angle in degrees: 180 is half a frame. Default: 180. |
| `--samples <n>` | Moments averaged, 2 to 64. Default: 32. |
| `--off` | Turn it off on this track. |
| `--inherit` | Follow the sequence instead. |
| `--no-save` | Do not write the project back. |

### `jazz track set-mute <project> <track-id> <muted>`

Mute or unmute a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |
| `<muted>` | true to mute. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz track set-pan <project> <track-id>`

Set an audio track's balance, or a keyframe of it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |

| Option | Meaning |
|---|---|
| `--pan <number>` | Required. -1 hard left, 0 centre, 1 hard right. |
| `--at <time>` | Set a keyframe at this time on the sequence. |
| `--no-save` | Do not write the project back. |

### `jazz track set-solo <project> <track-id> <solo>`

Solo or unsolo a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |
| `<solo>` | true to solo. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz track set-sync-lock <project> <track-id> <sync-locked>`

Keep a track in sync with ripple edits on others.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |
| `<sync-locked>` | true to keep it in sync. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

### `jazz track set-volume <project> <track-id>`

Set an audio track's volume in dB, or a keyframe of it.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The track id. |

| Option | Meaning |
|---|---|
| `--db <number>` | Required. Volume in dB, -144 to 24. |
| `--at <time>` | Set a keyframe at this time on the sequence. |
| `--no-save` | Do not write the project back. |

## `jazz tracking`

Commands and queries about tracking.

### `jazz tracking apply <project> <track-id>`

Make a clip, an effect's point or a mask follow a tracked point.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The point track id. |

| Option | Meaning |
|---|---|
| `--to <to>` | Required. The clip, effect or mask that follows. |
| `--param <param>` | Which parameter follows, when not the usual one. |
| `--absolute` | Put it on the point rather than keeping its distance. |
| `--no-save` | Do not write the project back. |

### `jazz tracking point <project> <clip-id>`

Track a point of a clip's picture through its frames.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--at <time>` | Required. The moment the point is picked, on the sequence. |
| `--x <number>` | Required. The point across, in source pixels. |
| `--y <number>` | Required. The point down, in source pixels. |
| `--size <n>` | The square followed, in pixels. Default: 31. Default: 31. |
| `--search <n>` | How far it may move in a frame, in pixels. Default: 48. Default: 48. |
| `--direction <both|forward|backward>` | both, forward or backward. Default: both. Default: both. |
| `--id <id>` | A track to re-track from here, or the id for a new one. |
| `--name <name>` | What to call a new track. |
| `--no-save` | Do not write the project back. |

### `jazz tracking remove <project> <track-id>`

Remove a point track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<track-id>` | The point track id. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

## `jazz transition`

Transitions on cuts: add, change, remove, and the default ones.

### `jazz transition add <project> <left-clip-id> <right-clip-id>`

Put a transition on the cut between two clips.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<left-clip-id>` | The outgoing clip id. |
| `<right-clip-id>` | The incoming clip id. |

| Option | Meaning |
|---|---|
| `--type <type>` | Which transition, for example transition.crossfade. |
| `--dur <time>` | How long it runs. |
| `--alignment <centered|end-of-left|start-of-right>` | centered, end-of-left or start-of-right. Default: centered. |
| `--handles <refuse|trim|hold>` | When a clip has too little source past the cut: refuse, trim or hold. Default: refuse. |
| `--audio` | Also crossfade the linked sound at the same cut. Default: true. |
| `--id <id>` | The identifier to give it. |
| `--no-save` | Do not write the project back. |

### `jazz transition add-all <project>`

Put a transition on every cut of a track.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--track <id>` | Required. The track. |
| `--type <type>` | Which transition, for example transition.crossfade. |
| `--dur <time>` | How long each runs. |
| `--alignment <centered|end-of-left|start-of-right>` | centered, end-of-left or start-of-right. Default: centered. |
| `--handles <refuse|trim|hold>` | When a clip has too little source past a cut: refuse, trim or hold. Default: hold. |
| `--audio` | Also crossfade the linked sound at the same cuts. Default: true. |
| `--no-save` | Do not write the project back. |

### `jazz transition apply-default <project>`

Put the default transition on the cut nearest a time.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--at-cut <time>` | Required. A time at or near the cut. |
| `--track <id>` | Only this track. |
| `--kind <both|video|audio>` | video, audio or both. Default: both. |
| `--handles <refuse|trim|hold>` | When a clip has too little source past the cut: refuse, trim or hold. Default: hold. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz transition list <project>`

List transitions and where they play.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--track <id>` | Only this track. |
| `--sequence <id>` | Which sequence. |

### `jazz transition remove <project> <transition-id>`

Remove a transition.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<transition-id>` | The transition id. |

| Option | Meaning |
|---|---|
| `--linked` | Also remove the one on the linked picture or sound at the same cut. Default: true. |
| `--no-save` | Do not write the project back. |

### `jazz transition set <project> <transition-id>`

Change a transition's type, duration or alignment.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<transition-id>` | The transition id. |

| Option | Meaning |
|---|---|
| `--type <type>` | A different transition of the same kind. |
| `--dur <time>` | How long it runs. |
| `--alignment <centered|end-of-left|start-of-right>` | centered, end-of-left or start-of-right. |
| `--linked` | Also change the linked transition's duration and alignment. Default: true. |
| `--no-save` | Do not write the project back. |

### `jazz transition set-default <project> [type]`

Set the transition the default shortcuts add.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<type>` | The transition type. Optional. |

| Option | Meaning |
|---|---|
| `--dur <time>` | How long the default transitions last. |
| `--no-save` | Do not write the project back. |

### `jazz transition set-param <project> <transition-id> <param> <value>`

Set a parameter of a transition.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<transition-id>` | The transition id. |
| `<param>` | The parameter name. |
| `<value>` | The value, for example 45, #000000 or ease-in-out. |

| Option | Meaning |
|---|---|
| `--no-save` | Do not write the project back. |

## `jazz trim`

### `jazz trim <file>`

Cut stretches out of one recording and write them back to back: by default a smart cut, exact and encoding only the frames around each cut.

| Argument | Meaning |
|---|---|
| `<file>` | The recording to trim. |

| Option | Meaning |
|---|---|
| `--keep <keep>` | The stretches to keep, in source time, as start-end pairs separated by commas: 00:10-00:25,01:00-01:30. Any time form works: 10s-25s, 600f-1500f. All of it when left out. |
| `--mute-stream <mute-stream>` | Sound streams to leave out, by container index or title: 2, or Mic, or 2,3. |
| `--out <out>` | Required. Where to write the file. The extension picks the container: .mp4, .mkv or .mov. |
| `--mode <mode>` | auto (smart when the source allows it, copy otherwise), smart (exact cuts, only the frames around each cut encoded again), copy (lossless, cuts on keyframes) or full (exact cuts, everything encoded again). Default: auto. |
| `--exact` | For a copy, refuse cuts that are not on keyframes instead of moving them to the nearest. |
| `--preset <preset>` | The preset for an encode. Default: youtube-1080p. |
| `--use-external-ffmpeg` | Encode through ffmpeg.exe, to tell an encoder problem from ours. |
| `--dry-run` | Print the plan and write nothing. |
| `--save <save>` | Also save the Quick Trim as a project, to open in the editor. |
| `--size <size>` | Fit the picture inside this size, for example 1280x720. Never scales up. |
| `--fps <fps>` | Write at this frame rate, for example 30 or 30000/1001. A slower rate takes every nth frame. |
| `--quality <quality>` | Constant quality: CRF or CQ, lower is better. |
| `--bitrate <bitrate>` | A picture bitrate instead of constant quality: 8M, 2500k. |
| `--encoder <encoder>` | The encoders to try, in order, comma separated: libx264 or hevc_nvenc,libx265. |
| `--audio-encoder <audio-encoder>` | The sound encoder: aac, libopus, flac, eac3, ac3, libmp3lame, pcm_s24le or pcm_s16le. |
| `--audio-bitrate <audio-bitrate>` | The sound bitrate: 320k. |
| `--channels <channels>` | 1, 2 or 6 channels. A 5.1 sequence exported in stereo is folded down (ITU), per source, before the master limiter. |
| `--loudness <loudness>` | Measure the mix and bring it to this integrated loudness, peaks held under -1 dBFS: -14. |
| `--target-size <target-size>` | Come in under this size: 8MB, in binary units as Discord counts. The picture steps down when it must; the file is checked and encoded again if it is over. |
| `--pixel-format <pixel-format>` | The pixel format: yuv420p10le for ten bits, yuv422p10le for 4:2:2 ten bit. Always BT.709. |
| `--audio-only` | Write the sound alone, in a sound file for the preset's encoder: AAC in .m4a, Opus in .opus, FLAC, WAV or MP3. |
| `--start <start>` | Export from here, in sequence time. |
| `--end <end>` | Export to here, in sequence time. |

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

### `jazz trim add-segment <project>`

Keep a stretch of a Quick Trim.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--in <time>` | Required. Where the stretch starts. |
| `--out <time>` | Required. Where it ends. |
| `--sequence <id>` | The Quick Trim sequence. |
| `--no-save` | Do not write the project back. |

### `jazz trim remove-range <project>`

Cut a range out of a Quick Trim.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

| Option | Meaning |
|---|---|
| `--in <time>` | Required. Where the cut starts. |
| `--out <time>` | Required. Where it ends. |
| `--sequence <id>` | The Quick Trim sequence. |
| `--no-save` | Do not write the project back. |

### `jazz trim set-segments <project> <keep>`

Keep exactly these stretches of a Quick Trim.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<keep>` | The stretches to keep, as start-end pairs. |

| Option | Meaning |
|---|---|
| `--sequence <id>` | The Quick Trim sequence. |
| `--no-save` | Do not write the project back. |

### `jazz trim start <project> <media-id>`

Start a Quick Trim of a file.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<media-id>` | The media id. |

| Option | Meaning |
|---|---|
| `--id <id>` | The id for the new sequence. |
| `--name <name>` | The sequence's name. |
| `--no-save` | Do not write the project back. |

## `jazz vfx`

Commands and queries about vfx.

### `jazz vfx apply-preset <project> <preset>`

Place a timed combination of effects (a hit, a heavy hit, a boss intro) at a moment or a marker.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<preset>` | The preset, for example impact.heavy; vfx.presets lists them. |

| Option | Meaning |
|---|---|
| `--at <time>` | The moment of the hit, on the sequence. |
| `--marker <marker>` | A marker whose time is the moment, by name or id. |
| `--to <to>` | A clip or track to put the effects on, instead of a new adjustment clip. |
| `--sequence <id>` | Which sequence. |
| `--no-save` | Do not write the project back. |

### `jazz vfx find-static <project> <clip-id>`

Find what stands still over a clip's moving picture (a game HUD, a watermark, debug text), as regions to hide.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--samples <n>` | How many frames are compared. Default: 24. Default: 24. |

### `jazz vfx hide-static <project> <clip-id>`

Hide a clip's HUD or watermark by blur, pixelate, fill from a clean frame, or crop; finds the regions when none are given.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |
| `<clip-id>` | The clip id. |

| Option | Meaning |
|---|---|
| `--how <blur|pixelate|fill|crop>` | blur, pixelate, fill or crop. Default: blur. Default: blur. |
| `--regions <regions>` | x,y,width,height in source pixels, several separated by ;. |
| `--plate <time>` | fill: the moment the clean frame is taken from. |
| `--no-save` | Do not write the project back. |

### `jazz vfx presets <project>`

The effect presets vfx.apply-preset places, with what each is made of.

| Argument | Meaning |
|---|---|
| `<project>` | The .jazz file to work on. |

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
