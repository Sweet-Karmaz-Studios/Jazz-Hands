# Recovery

When the editor closes without saving (a crash, a power cut, a killed process), what was done since the last save is not lost. Every command since the project was saved or opened is in `<name>.jazz.d/history.jsonl`, with the identifiers it made, and autosave writes a copy every minute.

`recovery_check` says whether there is anything to bring back and how much, and lists projects that were never saved but were rescued when the editor crashed. `recovery_accept` rebuilds the project by replaying every command over the saved file (falling back on the autosave copy if a command cannot be replayed); the project is then unsaved, and the file itself is untouched until you save. `recovery_accept` with `file` brings back a rescued untitled project. `recovery_discard` sets the recovery files aside in `<name>.jazz.d/recovered` and keeps the project as saved. Starting to edit without choosing sets them aside too.
