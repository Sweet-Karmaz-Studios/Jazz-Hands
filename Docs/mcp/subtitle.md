# Subtitles

Subtitles live on subtitle tracks, one clip per cue, with the words as markup (`[i]italic[/i]`, `\n` for a new line) and a place on the frame (`align`, bottom by default). A track's cues share its style (`subtitle_set_style`: font, size as a fraction of the frame height, colour, outline, box, shadow, margin, line limits) and its language (`track_set_language`).

- `subtitle_import` reads SubRip, WebVTT or ASS files, or a subtitle stream inside a media file, onto a new or existing track; `offset` moves every cue.
- `subtitle_add`, `subtitle_set_text`, `subtitle_set_time` and `subtitle_set_align` work on single cues; `subtitle_shift` moves many; `subtitle_replace` finds and replaces text; `subtitle_split_long` breaks cues that are too long to read.
- `subtitle_list` shows a track's cues; `subtitle_read` shows a file's without importing it; `subtitle_export` writes a track as SRT, VTT or ASS.

On export (`export_enqueue`'s `subtitles`), cues can be soft (a stream the player can turn off), burned into the picture, or written beside the file as a sidecar.
