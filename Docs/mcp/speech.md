# Speech

What is said in the footage, as words with their times, heard on this machine by whisper (large-v3-turbo). Nothing leaves the computer.

- `speech_transcribe` hears a media item, a clip's file, or every sounding clip of a sequence. A transcript is of the whole file and kept in the cache by the file's content, so every clip of it shares it and asking again is instant. It needs the speech model: `model_list` says whether it is downloaded, and `model_download` fetches it (1.6 GB), which you ask the person about first. Ten minutes take about fifteen seconds on the GPU.
- `speech_transcript` gives the words of a clip or a sequence at their timeline times, each with its index in its clip. Its `text` is the transcript as a reader reads it: a paragraph per clip, a line per sentence, each word after its index in brackets (`[12]jump.`), long pauses shown.
- The resource `jazz://transcript` is the same text for the active sequence, and `jazz://transcript/<clipId>` for one clip: read it, then cut by it.

## Editing by text

- `clip_remove_words` cuts words out of a clip by their indices, `from` to `to`, with the pause after them, each edge on the nearest frame, and closes the gap on the clip's track, its linked sound, the subtitle tracks and every sync-locked track. One undo. Indices change after a cut, so read the transcript again before the next one, or cut the latest words first.
- `clip_find_fillers` lists the filler words (um, uh and the like; `words` to name others, such as `like`) and, with `pauses`, the pauses longer than that; `clip_remove_fillers` with the same arguments cuts them all as one undo. Find first, show the person, then remove.
- `subtitle_from_transcript` lays the words out as captions on a subtitle track (sentences, pauses, balanced lines, time to read), and `subtitle_check` checks them: line length, reading speed, time on screen and gaps.

A word's index is its place among its clip's words, so after a cut splits a clip, the words after the cut belong to the new clip, numbered from 0.
