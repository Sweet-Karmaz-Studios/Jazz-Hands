# Markers

A marker is a named point on a sequence (or on a clip, travelling with it), with an optional colour, note and duration; a marker with a duration marks a range. Markers are for finding your way: beats of the music, a moment to cut to, a note for the person you are working with. `playback_go_to` jumps between them.

A marker with `isChapter` becomes a chapter in exported files (see jazz://docs/chapter). Range markers can each become an export with `export_batch` and `markers: true`.

`marker_list` gives them in time order with their ids; `marker_set` changes one; `marker_remove` deletes one.
