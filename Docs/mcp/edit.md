# Editing to the beat

`edit_cut_to_beats` is the trailer montage in one call: it takes the sequence's beat markers (from `audio_beats` on a music clip), keeps every `every`th between `from` and `to` (or only the downbeats), and fills each stretch between two of them with the next clip, overwriting what the track had there. The clips come from `clips` (clips already on a timeline, each played from where it starts in its source) or from the media in a bin folder (`bin`), each from its start. Their own sound is left off unless `withAudio`, since the music is what cuts them.

A cut every bar in four is `every: 4`; `downbeats: true` cuts only on the first beat of each bar. It stops when the stretches or the clips run out. One undo.
