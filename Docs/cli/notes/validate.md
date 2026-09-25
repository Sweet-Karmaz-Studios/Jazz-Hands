Checks a project against the generated schema and then against the semantic rules, and prints one
line per problem as `path: severity: code: message`. Errors go to stderr, warnings to stdout.

```bash
jazz validate trailer.jazz
```

```
/sequences/0/tracks/0/kind: error: not-a-member: Value should match one of the values specified by the enum
```

Exit code 0 when the project loads, 1 when it does not, or when `--strict` and anything at all was
found. This is the only verb that runs schema validation; the others skip it because it costs more
than the rest of opening a project put together.

With `--json`: one object with `loadable`, `errors`, `warnings` and the `issues` array.
