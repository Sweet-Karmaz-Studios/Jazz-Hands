# Looking at the result

These are your eyes. Editing without looking is guessing; look after anything visual.

- `render_frame` returns one frame as a PNG exactly as the editor's preview draws it, at the width you ask (960 by default; the height follows the sequence). It takes a few tens of milliseconds, so use it freely: after placing a title, after a transform or crop, at each side of a cut.
- `contact_sheet` tiles frames at even steps across a sequence (or a stretch) into one labelled image: the shape of a whole edit at a glance, good for pacing, repetition and shot order.
- `session_info` says what is open, where it lives, whether it is saved, and who else is connected and who holds the lock.

For sound, `audio_meter` measures a stretch of the mix. For the whole thing, `render_proof` makes a watchable file.
