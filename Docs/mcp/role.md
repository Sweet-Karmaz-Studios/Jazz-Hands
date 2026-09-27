# Roles

Every track has a role: what it is for. The project starts with Dialogue, Music, Effects, Game, Video and Titles, and can have its own (`role_add`). A track with no role set has the one its kind and name suggest: an OBS capture's Game track is Game and its Mic track Dialogue, a picture track Video. `role_list` says which tracks of the active sequence have each.

`track_set_role` gives a track a role; `role_rename` and `role_remove` carry its tracks along. `role_mute` and `role_solo` act on every track of a role at once, the way a mixer's groups do, and are undoable like any edit. Export can write one file per role (stems) beside the mix; see the export area.
