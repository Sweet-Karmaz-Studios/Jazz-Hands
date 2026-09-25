# Media

Media is the files a project uses: movies, sound, images and image sequences. `media_add` imports files, folders or globs (`C:\captures\*.mp4`); each becomes a media item with an id, read once for its streams, duration, frame rate and colour. The project stores paths relative to itself where it can, so a project folder can move.

- `media_list` finds items by folder, tag or text; `media_get` describes one and its streams (stream numbers matter for `audio_mute_stream` and `clip_add`'s `sourceStreamIndex`).
- `probe_media` reads a file without importing it: use it to choose what to bring in.
- `media_set` renames, files, tags and colours items, and sets how a picture that does not match the sequence is fitted (`conform`: fit, fill, stretch or native).
- `media_relink` points an item at a file that has moved; `media_reprobe` reads a changed file again.
- `media_remove` takes an item out; with `withClips` the clips that play it go too, otherwise it is refused while clips use it.

A clip refers to its media by id, so import first, then place: `media_add` answers with the new ids in `changedIds`.
