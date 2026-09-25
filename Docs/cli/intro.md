# jazz command reference

This file is generated: `jazz docs --markdown --out Docs/CLI.md` writes it from the commands
themselves, so every verb, argument and option here is the one `jazz` parses, with the
description `--help` prints. Change a description in the code, or the prose in `Docs/cli`
(`intro.md`, `exit-codes.md`, `guides.md`, and a note per verb in `notes/`), and regenerate. A
test fails when this file is out of date.

See the `cli-conventions` skill for the rules the CLI follows, and "Guides" at the end for how
the verbs fit together.

```
jazz <verb> [arguments] [--options]
jazz <area> <verb> <project.jazz> [arguments] [--options]
```

Most verbs are generated from the command registry: a command or query `area.verb` is
`jazz area verb`, spelled as it is over JSON-RPC and to MCP. The project comes first. Queries
print their answer as readable text, and as JSON with `--json`. Times take every form
`Timecode.Parse` reads (`00:00:04:12`, `00:00:04.500`, `4.5s`, `135f`, `3175200000fl`), at the
active sequence's rate.
