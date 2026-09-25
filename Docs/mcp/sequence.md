# Sequences

A sequence is a timeline: tracks of clips, markers, in and out points and a master mix. A project has one or more, and one is active: the one the editor shows, and the one tools use when they are not told `sequenceId`.

- `sequence_create` adds an empty one (with `setActive` to show it); `sequence_set_active` switches; `sequence_rename` and `sequence_remove` do what they say.
- `sequence_set_settings` gives a sequence its own frame rate, size or audio format (a vertical cut for phones beside the landscape one, for example); `inherit` goes back to the project's.
- `sequence_list` gives each sequence's id, name, duration and settings.

A sequence can also be a clip in another: `clip_add` with `sequenceId` nests it, and `clip_nest` makes one from clips.

For Shorts, Reels and TikTok, `sequence_reframe` makes a vertical version (1080x1920 by default) that nests the sequence: a window cut from it in front (`window` is its shape; its clip's position in the window sequence is the pan, or `follow` a point track) over a blurred copy, or with `mode: fit` the whole picture across the width. Titles and other graphics are lifted over the vertical frame rather than cut. Templates placed in it use their portrait layouts; export with the `youtube-shorts`, `reels` or `tiktok` presets.
