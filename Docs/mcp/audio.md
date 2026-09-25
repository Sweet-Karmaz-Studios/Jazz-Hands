# Sound

Every audio clip plays through its track into the sequence's master, which has a volume and a true-peak limiter. Levels are in dB: 0 leaves a sound as it is, -6 is about half as loud, and the limiter's ceiling keeps the mix under a peak (-1 dBTP by default when on).

On clips:

- `audio_set_gain` sets a clip's level; with `at` it sets a keyframe instead, so two keyframes make a rise or a dip (ducking music under a line of dialogue is two or four keyframes).
- `audio_set_fade_in` and `audio_set_fade_out` fade a clip's start and end, with a curve.
- `audio_set_pan` places a clip left or right (-1 to 1).
- `audio_mute_stream` silences one stream of a clip whose media has several (a game capture's microphone and game sound, for example); `media_get` lists the streams.
- `audio_set_channel_map` plays one side of a stereo recording as mono; `audio_replace` plays another item's sound in a clip; `audio_detach` unlinks a clip's sound from its picture.

On the mix: `audio_set_master_volume` and `audio_set_limiter` for the sequence, and `track_set_volume` for a track. `audio_meter` measures a stretch: peak, true peak, RMS and integrated loudness (LUFS). Online video aims for about -14 LUFS with peaks under -1 dBTP; `export_enqueue` can also normalise loudness as it writes.

Effects on sound (EQ, compressor, gate, de-esser, limiter, reverb, delay) are `effect_add` with an `audio.` type on a clip or an audio track.

`audio_beats` finds a music clip's beats and tempo and marks them on the sequence as beat markers, downbeats flagged; `audio_beat_analysis` answers without marking. Cut to them with `edit_cut_to_beats` (jazz://docs/edit).
