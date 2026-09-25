# Playback

In the editor, these move the playhead and play the preview the person is watching; headless there is no player and they are refused with `no-playback`. None of them changes the project, except the in and out points.

- `playback_seek` puts the playhead at a time; `playback_step` moves by frames; `playback_go_to` jumps to the start, the end, the next or previous edit or marker, or the in or out point.
- `playback_play`, `playback_pause`, `playback_toggle` and `playback_stop`; `playback_shuttle` plays at a rate (2 is double speed, -1 backwards).
- `playback_set_in` and `playback_set_out` mark a range (for `export_enqueue` with `useInOut`, and for looping with `playback_loop`); `playback_clear_in_out` removes them. These are part of the sequence and undoable.
- `playback_state` says where the playhead is and what is happening; `playback_set_quality` chooses the preview's resolution.

To see a frame, `render_frame` is better than seeking: it returns the picture.
