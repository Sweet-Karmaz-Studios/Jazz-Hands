# Tracks

A sequence's tracks hold its clips. There are four kinds: `video` (pictures; a higher track draws over a lower one), `audio` (every audio track is mixed), `subtitle` (one cue per clip), and `adjustment` (its effects apply to the picture of every track below it). A new project has one video and one audio track; `track_add` adds more and says where in the stack with `order`.

The switches:

- `track_set_mute` silences an audio track or hides a video track; `track_set_solo` plays only soloed tracks.
- `track_set_lock` stops edits changing a track; locked tracks are skipped by range edits such as `clip_lift`.
- `track_set_sync_lock` decides whether ripple edits on other tracks move this one too, which keeps music and dialogue in step with the picture. On by default.
- `track_set_volume` and `track_set_pan` set an audio track's level (dB) and balance, or a keyframe of either with `at`.
- `track_set_language` tags what a track says (ISO 639-2, such as `eng`), which exports write into the file.

`track_list` gives every track's id, kind, name and order. `track_remove` removes a track and every clip on it.
