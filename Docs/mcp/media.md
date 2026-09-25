# Media

Media is the files a project uses: movies, sound, images and image sequences. `media_add` imports files, folders or globs (`C:\captures\*.mp4`); each becomes a media item with an id, read once for its streams, duration, frame rate and colour. The project stores paths relative to itself where it can, so a project folder can move.

- `media_list` finds items by folder, tag or text; `media_get` describes one and its streams (stream numbers matter for `audio_mute_stream` and `clip_add`'s `sourceStreamIndex`).
- `probe_media` reads a file without importing it: use it to choose what to bring in.
- `media_set` renames, files, tags and colours items, and sets how a picture that does not match the sequence is fitted (`conform`: fit, fill, stretch or native).
- `media_check` says which files are there, missing or changed on disk. `media_missing` looks for the missing ones (by hash, then by name and size, then by name) and lists candidates; `media_relink` with `auto` relinks every one found, as one undo, and with an id and a path relinks one by hand. `media_reprobe` reads a changed file again. A clip whose file is missing shows a red "Media offline" slate in frames and exports.
- `media_usage` says which clips and sequences use each item and how much of it; `media_remove_unused` takes out what nothing uses. `media_replace` swaps an item's file for another (a better take) keeping its clips.
- `media_watch` watches a captures folder in a running editor and brings in each new recording once it has finished being written, tagged by subfolder and day; `media_watches` lists them, `media_unwatch` stops.
- `media_analyze_motion` analyses how the camera moves through a video, for `clip_stabilize`, ahead of time or again.
- `media_remove` takes an item out; with `withClips` the clips that play it go too, otherwise it is refused while clips use it.

A clip refers to its media by id, so import first, then place: `media_add` answers with the new ids in `changedIds`.
