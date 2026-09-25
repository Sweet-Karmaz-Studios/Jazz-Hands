# Keyframes

Any parameter of a clip, track, effect or mask can change over time: give it keyframes, and between two keyframes the value moves from one to the other along a curve. A parameter with no keyframes holds one value.

- `keyframe_add` puts a keyframe at a time with a value (or the value it has there already). Two keyframes make a move; a fade from invisible is opacity 0 at the clip's start and 1 half a second later.
- `keyframe_set_interp` chooses how the curve leaves a keyframe: `hold` (jump at the next keyframe), `linear`, `easeIn`, `easeOut`, `easeInOut` or `bezier` (then `keyframe_set_handles` shapes it).
- `keyframe_move`, `keyframe_set_value` and `keyframe_remove` change one keyframe, found by its time; `param_clear_keyframes` removes them all and keeps one value.

Times are on the sequence unless `local` is true, when they count from the clip's own start, which is handier for a move that should travel with the clip. `param_set`, `effect_set_param`, `audio_set_gain` and the other setters take `at` to add or change a keyframe instead of the whole value.
