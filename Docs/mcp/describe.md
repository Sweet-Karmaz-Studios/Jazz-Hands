# Describing the project

`describe_timeline` is the best first call and the best check after edits: one page of text with the project's settings, its media, the active sequence's tracks with every clip in order (name, times, source), gaps, transitions, markers and problems such as missing media or clips past the end of their source. At `brief` it keeps to about 2000 tokens by summing up the middle of long tracks; ask for a `range` to see a stretch in full, or `detail: "full"` for ids, sources, effects and every problem.

Read ids from it with `detail: "full"`, then act on them. After a batch of edits, describe again rather than keeping the project in your head: the person may have changed something too.
