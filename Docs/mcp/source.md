# Source monitor

The source monitor is the second viewer: it plays one media item, not the sequence, and its in and out marks say which stretch of that file a three-point edit takes. Like the selection, it is the session's view state: not part of the project and not undoable, but shared, so the marks you set are the ones the person sees, and theirs are the ones you read with `source_state`.

A three-point edit the way a person does it: `source_open` a media item, `source_set_in` and `source_set_out` (with `at` in source time, or at the source playhead), then `clip_insert_from_source` (pushes the rest along) or `clip_overwrite_from_source` (replaces what is there) at the program playhead or the sequence in point. `clip_plan_from_source` says what either would do first, including when four marks were fitted to the shorter range. Every edit option can be given outright (`media`, `source_in`, `source_out`, `at`), so the same edit works headless with no source monitor at all.

Which tracks take the picture and the sound is the source patching: `track_set_target`. `sequence_find_source_frame` is match frame the other way, from a source frame to where the sequence shows it. `source_play` needs a running editor; everything else works in `jazz serve` too.
