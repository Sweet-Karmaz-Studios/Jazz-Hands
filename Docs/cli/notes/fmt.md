Rewrites a project into canonical form: declaration order, two space indentation, line feeds,
defaults left out, clips in start order and tracks in stacking order. Members this build does not
recognise are kept exactly as they were.

```bash
jazz fmt trailer.jazz --check
```


With `--json`: `{"path": ..., "rewritten": true}`.
