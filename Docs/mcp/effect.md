# Effects

An effect is a filter on a clip or a track: `effect_add` puts one at the end of the owner's chain (or at `index`), and the chain runs in order. On a track, an effect applies to every clip on it; on an adjustment track, to everything below. `list_effects` names every type with its parameters, ranges and defaults.

Picture effects are `video.*` (blur, sharpen, glow, crop, drop shadow, vignette, keys, distortion, Ken Burns and more) and `color.*` (basic, wheels, curves, HSL, LUT, white balance). Sound effects are `audio.*` (gain, parametric EQ, compressor, gate, de-esser, limiter, reverb, delay). Generators (`gen.solid`, `gen.gradient`, `gen.countdown`, shapes, noise, timecode) are clips of their own: `clip_add` with `generatorId`.

- `effect_set_param` sets a parameter; values are numbers, `"x, y"` pairs or colours such as `#FF8800`. With `at` it sets a keyframe (see jazz://docs/keyframe).
- `effect_set_enabled` bypasses an effect without losing its settings; `effect_reset` puts defaults back; `effect_move` reorders the chain; `effect_remove` deletes one.
- `effect_copy` and `effect_paste` carry effects between clips; `effect_save_preset` keeps a chain as a named preset for `effect_apply_preset`.

`param_list` with the effect's id shows its parameters as they stand. Look at the result with `render_frame`.
