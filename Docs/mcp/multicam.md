# Multicam

A multicam clip is several recordings of the same moment (two cameras and a screen capture, a game capture and a face camera, three podcast microphones) lined up in a sequence of their own, an angle each, and cut between by switches. On the timeline it is one clip; what it shows and plays at each moment is the angle its switches say.

`multicam_sync` lines recordings up without making anything: by their sound (the default: the same moment heard in each), by the timecode each starts at, by their in points, or by the first marker on each file (a clap). It gives each recording's start and how sure the match is, and names the weakest. `multicam_create` does the same and makes the clip, on the lowest picture track at its end unless told otherwise; a match by sound under 0.3 is refused without `force`.

`multicam_switch` cuts the picture, the sound or both to an angle (numbered from 1) from a time on; `multicam_set_sound` keeps the sound on one angle whatever the picture. `multicam_flatten` turns the switches into ordinary clips, picture and sound linked, as if the cuts had been made by hand; undo brings the multicam back. `multicam_view` shows a clip's angles as a grid in a running editor's program monitor, where the person clicks an angle or presses 1 to 9 while it plays.
