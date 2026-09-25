# Parameters

Everything that can be set on a clip, track, effect or mask is a parameter with a name: a clip has `opacity`, `transform.position`, `transform.scale`, `transform.rotation` and more; an effect has its own (`radius`, `amount`, `color`). `param_list` with the owner's id lists them with their current values, ranges and whether they are keyframed.

- `param_set` sets one. Values are written as text: a number (`12`), a pair (`"100, 50"`), a colour (`#FF8800`) or a word for a choice. With `at`, it sets a keyframe there rather than the whole value.
- `param_get` shows one, and what it is worth at a time.
- `param_clear_keyframes` turns an animated parameter back into one value.

The named setters (`clip_set_opacity`, `clip_set_transform`, `effect_set_param`, `audio_set_gain`) are shortcuts to the same parameters; use whichever reads better. See jazz://docs/keyframe for animation.

A parameter can be driven by an expression instead of keyframes: `param_set_driver` with, for example, `value * (1 + audio("Music", low) * 0.15)` for a logo that pulses on the kick, `value + wiggle(2, 12)` for a title that wiggles, or `0.3 + audio("Music", level, 0.5, 2) * 0.6` for a vignette that tightens as the music builds. The language has `time`, `frame`, `value` (the keyframes or value underneath), `wiggle(freq, amount, seed)`, `noise`, `loop(period)`, `pingpong(period)`, `audio(track, level|low|mid|high, attack, release)`, `param(id, name)`, `marker(name)`, arithmetic, `[x, y]` vectors and the usual maths functions; an expression that does not read is refused with the character it went wrong at. `param_clear_driver` goes back to what was underneath.
