# Selection

The selection is which clips, markers and tracks the editor has highlighted (a track, selected by a click on its header, shows its own volume, pan and effects in the Inspector). It is not part of the project and not undoable, but it is shared: what you select, the person sees selected, and what they select you can read with `selection_get`. That makes "these clips" easy to talk about: ask the person to select what they mean, then read it.

`selection_set` replaces, adds to, removes from or toggles the selection; `selection_clear` empties it.
