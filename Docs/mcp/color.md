# Colour

Colour is corrected with effects: `color.basic` (exposure, contrast, saturation, temperature), `color.wheels` (lift, gamma, gain), `color.curves`, `color.hsl` (change one range of hues), `color.lut` (a .cube look) and `color.white-balance`. Add them with `effect_add` to a clip, or to an adjustment track to grade everything under it.

To judge colour, measure rather than guess: `color_sample` reads the colour at a point of a frame (averaged over a small square), and `scopes_measure` gives a frame's histograms and how much of it is clipped at black or white. `render_frame` shows the result as the preview draws it.

HDR footage is brought down to SDR by a tone mapping operator (`project_set_tone_map`, or `clip_set_tone_map` for one clip).

To make one shot look like another, `color_match` grades a clip's Colour Wheels (lift, gamma, gain and saturation; it adds the effect when there is none) so its tones and colours spread as the reference clip's do. It reads one frame of each clip on its own, the middle unless `at` and `reference_at` say otherwise, and is one undo step. Check the result with `render_frame`, or measure it with `scopes_measure` on both clips.
