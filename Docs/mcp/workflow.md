# Working in Jazz Hands

Jazz Hands is a video editor, and these tools are the whole of it: every edit a person makes with the mouse is one of these commands, and every command can be undone. When you are attached to the editor (`jazz mcp --attach`), the person is watching the same timeline; what you change appears there at once, outlined for a second.

## The loop

1. **Read.** `describe_timeline` gives the project in a page: settings, media, each track's clips in order with their times, gaps, transitions, markers and problems. `detail: "full"` adds ids, sources and effects. Read it before you edit and again after every few edits.
2. **Edit.** Call the tool for each change (`clip_add`, `clip_split`, `title_add`, `audio_set_gain`...). For several changes at once use `apply_batch`: without references between its steps it is one undo step, all or nothing.
3. **Look.** `render_frame` returns the frame at a time exactly as the preview draws it. Look after anything visual: titles, transforms, crops, colour, effects. `contact_sheet` shows a whole edit in one image, good for pacing and shot order.
4. **Watch.** `render_proof` exports a quick 480p proof and waits for it. `audio_meter` measures loudness over a stretch.
5. **Fix and repeat.** If a step was wrong, `undo` takes it back; `history` lists what was done and by whom.

## Times

A time is a string: `"00:00:02.500"` (hours, minutes, seconds), `"2.5s"`, `"75f"` (frames at the sequence's rate), or an integer of flicks (705,600,000 a second). A range is `"00:00:10.000-00:00:25.000"` or `"10s-25s"`. Times on a clip's parameters are sequence times unless `local` is true.

On the timeline, `at` is where something starts on the sequence. For a clip, `sourceIn` is where in its media it starts playing and `duration` how long it runs, so a clip at `4s` with `sourceIn` `70s` and `duration` `12.5s` shows the media from 1:10 to 1:22.5 between 4 s and 16.5 s of the sequence.

## Ids

Everything has an id, a 26 character string such as `01J9Z3K4M5N6P7Q8R9S0T1V2CA`. A command answers with `changedIds`: what it made or touched, with the new thing first. `describe_timeline` with `detail: "full"`, `clip_list`, `track_list`, `media_list` and `marker_list` give the ids of what is already there. Most commands that make something take an optional id (`clipId`, `trackId`, `markerId`) so you can name it yourself and refer to it later in the same batch.

In `apply_batch`, a step can name what it made with `"as": "shot"` and a later step can use `"$shot.id"` (its first changed id) or `"$shot.ids[1]"`; `"$last.id"` is the step before. A batch with references runs step by step while holding the editor, each step its own undo.

## Tracks and layers

A sequence has tracks: video (higher tracks draw on top), audio, subtitle and adjustment (its effects apply to everything below). A new project has one video and one audio track. Clips of the same track never overlap; putting one where another is either refuses, pushes (`clip_insert`) or covers (`clip_overwrite`).

Adding a clip from a movie with sound puts its sound on the audio tracks too, linked to the picture (`withAudio`, true by default); moving or trimming one moves the other. `audio_detach` unlinks them.

## Working alongside a person

- Say what you are about to do before a large change, and what you did after.
- `jazz://events/recent` lists what happened in the editor, including what the person did by hand (issuer `gui`). Check it before editing if time has passed.
- Nothing is saved until `project_save`. The editor also keeps a recovery copy.
- Exports, the cache, playback and the selection are not undoable: they do not change the project.

## Errors

A tool that is refused returns an error result with a code and a sentence, such as `clip-not-found: ...` or `would-overlap: ...`, and changes nothing. Read the sentence: it usually says what to do instead.
