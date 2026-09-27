# The project

A project is one `.jazz` file: readable JSON holding the media list, the sequences with their tracks and clips, and the settings (frame rate, frame size, audio format, colour). `jazz://project` is the file as it would be saved; `jazz://schema` is its schema.

- `project_get` summarises it: settings, counts and duration. `session_info` says where it lives, whether it has unsaved changes and who else is connected.
- `project_set_settings` changes the frame rate, size or audio format; a sequence can have its own (`sequence_set_settings`). Frame rates are exact ratios: `30000/1001`, not 29.97 (though `"29.97"` is read as it).
- `project_set_tone_map` sets how HDR footage is brought down to SDR by default.
- `project_save` writes it, atomically; `path` saves as. Nothing is written until then.
- `project_consolidate` gathers the project and every file its clips use into one folder (`trim` keeps only the used parts, smart cut, with `handles`); `project_archive` writes the same into one zip. The open project is not changed. They take a while on long recordings.
- `project_new`, `project_open` and `project_sample` replace the open project, and are refused while it has unsaved changes unless `discard` is true. In the editor, that is the person's project: ask first. `project_sample` is a 20 second trailer of the editor's own gradients, particles, titles and sound, needing no media: somewhere to try a workflow before touching real footage.
