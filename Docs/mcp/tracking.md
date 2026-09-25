# Point tracking

`tracking_point` follows a small square of a clip's picture (a corner, an icon, a face) through its frames, forwards and backwards from the moment you pick it, to a fraction of a pixel, and keeps the path on the clip as a point track. Give the point in the clip's source pixels (x across, y down from the top left), as masks are. The track stops where the match falls below half: the point went behind something or out of frame. To correct it, pick the right place at a later frame and track again with the same `trackId` and a direction; the other side is kept.

`tracking_apply` makes something follow a track: a clip's `transform.position`, an effect's point (a callout's `target`, or the one named with `param`), or a mask on the tracked clip. By default what follows keeps its distance from the point; `absolute` puts it on the point. It writes a keyframe per tracked frame, which you can then edit. `sequence_reframe` can follow a track too, to keep a moving subject in a vertical crop.

`tracking_remove` deletes a track; what already follows it keeps its keyframes. Track ids are on the clip, in `clip_get`.
