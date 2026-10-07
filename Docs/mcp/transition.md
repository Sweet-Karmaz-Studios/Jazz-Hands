# Transitions

A transition plays across the cut between two touching clips on one track: `transition_add` takes the outgoing (`leftClipId`) and incoming (`rightClipId`) clips, a type and a duration. Types: `transition.crossfade`, `transition.dip` (through a colour, black by default), `transition.blur-dissolve`, `transition.wipe.linear`, `transition.wipe.clock`, `transition.wipe.iris`, `transition.wipe.radial`, `transition.push`, `transition.slide`, `transition.zoom` and `transition.glitch`. On audio tracks a crossfade blends the sound.

A transition needs media beyond the cut on both sides (handles). `alignment` places it centred on the cut, ending at it or starting at it; `handles` says what to do when a clip has too little: refuse, trim the clips to make room, or hold the edge frame.

- `audio: true` also crossfades the linked sound at the same cut.
- `transition_add_all` puts one on every cut of a track; `transition_apply_default` uses the default type at the cut nearest a time.
- `transition_set` changes type, duration or alignment; `transition_set_param` changes its own parameters (a dip's colour, a wipe's angle and softness).
- Its numbers, colours and points animate like any parameter: `keyframe_add` on the transition's id, times on the sequence, stored from where the transition starts so they move with it (a wipe's angle turning as it crosses). Choices such as direction or shape, and the easing, do not.

Transitions follow their clips: a split or a trim that moves the cut keeps it, and one whose clips no longer meet is removed in the same undo step. Use them sparingly; most cuts in a trailer are straight cuts.
