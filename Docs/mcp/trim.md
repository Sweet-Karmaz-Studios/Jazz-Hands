# Quick Trim

Quick Trim cuts the dull stretches out of one long recording and writes the rest back to back, usually without re-encoding (a smart cut: exact cuts, only the frames near each cut encoded again). It is how a game capture becomes a clip worth sharing in a minute.

`trim_start` makes a Quick Trim sequence from a media item, keeping everything. Then say what stays: `trim_set_segments` with the stretches to keep (`["00:01:10-00:01:25", "00:03:02-00:03:40"]`), or `trim_add_segment` and `trim_remove_range` one at a time. Export it with `export_enqueue`; `mode: "auto"` smart cuts when it can.
