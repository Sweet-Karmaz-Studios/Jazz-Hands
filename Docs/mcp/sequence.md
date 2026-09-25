# Sequences

A sequence is a timeline: tracks of clips, markers, in and out points and a master mix. A project has one or more, and one is active: the one the editor shows, and the one tools use when they are not told `sequenceId`.

- `sequence_create` adds an empty one (with `setActive` to show it); `sequence_set_active` switches; `sequence_rename` and `sequence_remove` do what they say.
- `sequence_set_settings` gives a sequence its own frame rate, size or audio format (a vertical cut for phones beside the landscape one, for example); `inherit` goes back to the project's.
- `sequence_list` gives each sequence's id, name, duration and settings.

A sequence can also be a clip in another: `clip_add` with `sequenceId` nests it, and `clip_nest` makes one from clips.
