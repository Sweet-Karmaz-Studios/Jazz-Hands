# Colour

Colour is corrected with effects: `color.basic` (exposure, contrast, saturation, temperature), `color.wheels` (lift, gamma, gain), `color.curves`, `color.hsl` (change one range of hues), `color.lut` (a .cube look) and `color.white-balance`. Add them with `effect_add` to a clip, or to an adjustment track to grade everything under it.

To judge colour, measure rather than guess: `color_sample` reads the colour at a point of a frame (averaged over a small square), and `scopes_measure` gives a frame's histograms and how much of it is clipped at black or white. `render_frame` shows the result as the preview draws it.

HDR footage is brought down to SDR by a tone mapping operator (`project_set_tone_map`, or `clip_set_tone_map` for one clip).

To make one shot look like another, `color_match` grades a clip's Colour Wheels (lift, gamma, gain and saturation; it adds the effect when there is none) so its tones and colours spread as the reference clip's do. It reads one frame of each clip on its own, the middle unless `at` and `reference_at` say otherwise, and is one undo step. Check the result with `render_frame`, or measure it with `scopes_measure` on both clips.

A grade can also be a node graph, as a colourist builds one: a `color.graph` effect whose nodes are Colour Wheels, Curves, HSL qualifier, LUT and White balance corrections, one after another or side by side and mixed. `color_node_add` adds one (to a graph, or to a clip, which then gets a graph), after a node or with `parallel_to` beside it; `color_node_connect` rewires one to read another, or with `key` to be limited by a qualifier node's matte; `color_node_remove` takes one out and reconnects what read it; `color_node_set` sets a node's parameter, a mix's `weights`, switches it off or makes it the `output`. `color_to_graph` turns an existing colour effect into a one-node graph without changing the picture. A node's id is an effect id, so `param_set`, keyframes and masks work on it directly. `effect_get` on the graph shows its nodes.
