# Clips

A clip is a stretch of media (or a generator, or another sequence) placed on a track. It has three times: `at`, where it starts on the sequence; `sourceIn`, where in its media it starts playing; and its duration. A clip's end is `at` plus its duration, and it shows the media from `sourceIn` for that long, sped up or slowed by its speed.

Clips on one track never overlap. `clip_add` refuses a place that is taken; `clip_insert` pushes what is there later on that track; `clip_overwrite` covers it. A movie's sound comes with its picture as linked clips on audio tracks (`withAudio`); moves, trims and splits of one apply to the other.

The edits, from simple to editorial:

- `clip_move` puts a clip at another time or track. `clip_nudge` moves by whole frames.
- `clip_split` cuts a clip in two at a sequence time; the right half gets a new id (the second of `changedIds`, or yours with `newClipId`).
- `clip_trim` moves a clip's start (`in`) or end (`out`) on the timeline, showing more or less of its media; `ripple` moves everything after it along.
- `clip_remove` takes a clip out, leaving a gap, or closing it with `ripple`. `clip_ripple_delete` does that for several clips across every sync-locked track.
- `clip_lift` and `clip_extract` remove a range of time from tracks: lift leaves the gap, extract closes it.
- `clip_roll` moves a cut between two touching clips; `clip_slip` changes which part of the source a clip shows without moving it; `clip_slide` moves a clip between its neighbours, trimming them.
- `clip_set_speed` changes speed (2 is double, 0.5 half), `clip_rate_stretch` fits a clip to a new length by speed, `clip_set_reverse` plays it backwards, `clip_freeze_frame` holds one frame.
- Speed curves: `clip_set_remap` turns time remapping on, after which the clip's `remap` parameter is its speed and `keyframe_add` shapes it (a clip and its linked sound share the curve); `clip_ramp_speed` puts a ramp from one speed to another over a stretch in one step. `clip_set_speed` is refused while a clip is remapped. `clip_set_retime` with `blend` crossfades the source frames either side of each moment for smoother slow motion (`optical-flow` blends too, for now).
- `clip_stabilize` steadies a shaky clip: the first time it analyses the file's camera motion (kept in the project's sidecar folder), then adds a Stabilize effect whose `smoothing`, `zoom` and `auto-zoom` can be changed later; `off` removes it.
- Picture: `clip_set_transform` (position in pixels from the frame centre, scale where 1 is fitted, rotation in degrees), `clip_set_crop` (percent off each edge), `clip_set_opacity` (0 to 1), `clip_set_blend`.

Linked clips move together; grouped clips select together. `clip_nest` turns clips into a sequence of their own shown as one compound clip; `clip_unnest` undoes that.

Speed: `clip_speed_preset` puts a packaged ramp on a clip at a moment: `impact` (slow into it, snap back), `traversal` (speed through `dur`), `rewind` (the seconds before, backwards and fast with a VHS look, on a track above) or `replay` (them again in slow motion with a label). `clip_set_motion_blur` blurs what a clip's animation moves; `clip_set_matte` shows it only through another track.

By what is said: `clip_remove_words` and `clip_remove_fillers` cut words, fillers and long pauses out of a clip (see jazz://docs/speech).

AI (on this computer): `clip_remove_background` cuts a person out of a clip without a green screen (Robust Video Matting, made once per file; choke, feather, invert and matte view on its `video.matte.person` effect). A missing model is refused with `model-missing`; ask the person before `model_download`.
