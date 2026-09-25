# Batches

`apply_batch` runs several commands in one call. Each step is `{"command": "clip.add", "args": {...}}`; the command may be written as the registry names it (`clip.add`) or as its tool (`clip_add`), and its args are what that tool takes.

When no step refers to another, the batch is sent as one command: it happens all at once, as one undo step, and if any step is refused nothing changes. This is the way to make a multi-part edit, such as laying out a sequence of shots, that the person can take back with one undo.

When a step needs what an earlier one made, name the earlier step with `"as": "shot"` and use `"$shot.id"` (its first changed id; `"$shot.ids[1]"` the second) or `"$last.id"` (the step before). Such a batch runs step by step while holding the editor so nobody edits in between; each step is its own undo step, and the first that fails stops the rest. To keep one undo step, give new things ids yourself instead (`clipId`, `trackId`, `markerId`) and refer to those.
