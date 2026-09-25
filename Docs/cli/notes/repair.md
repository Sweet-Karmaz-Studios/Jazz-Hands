Fixes the problems that have one obvious answer and reports the rest.

```bash
jazz repair trailer.jazz --dry-run
```

It removes clips whose media or nested sequence is not in the project, removes transitions whose
clips are gone, puts a zero speed back to 1/1, moves a clip that starts before the timeline, and
shortens fades that overrun their clip. It does not touch overlapping clips, because trimming
either one, moving either one and deleting either one are all defensible and a tool that guesses
is a tool nobody dares run.

Exit code 1 if an error-level problem is still there afterwards.

With `--json`: the `actions` taken and what is `remaining`.
