# Parameters

Everything that can be set on a clip, track, effect or mask is a parameter with a name: a clip has `opacity`, `transform.position`, `transform.scale`, `transform.rotation` and more; an effect has its own (`radius`, `amount`, `color`). `param_list` with the owner's id lists them with their current values, ranges and whether they are keyframed.

- `param_set` sets one. Values are written as text: a number (`12`), a pair (`"100, 50"`), a colour (`#FF8800`) or a word for a choice. With `at`, it sets a keyframe there rather than the whole value.
- `param_get` shows one, and what it is worth at a time.
- `param_clear_keyframes` turns an animated parameter back into one value.

The named setters (`clip_set_opacity`, `clip_set_transform`, `effect_set_param`, `audio_set_gain`) are shortcuts to the same parameters; use whichever reads better. See jazz://docs/keyframe for animation.
