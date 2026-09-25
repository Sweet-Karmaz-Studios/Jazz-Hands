# Settings

The editor's settings belong to the person using it, not to the project: the speakers, the GPU, the cache, the remote control server, closing to the notification area, notifications, recent projects. They live in one file for the machine, and the Settings dialog edits the same values.

`settings_get` lists every setting as `section.name` with its value, its default and what it takes; `section` narrows it to one (`editor`, `cache`, `control`, `recent`). `settings_set` changes one: the value is JSON, or plain text for text (`true`, `20`, `D:\\cache`). A setting that does not exist is refused with `unknown-setting`, and a value of the wrong kind with `bad-setting`, before anything is written.

A running editor applies what it can at once (the playback device, scrub sound, closing to the notification area, notifications, starting with Windows); the GPU, the cache folder and the control server apply from its next start. Settings are not undoable: to go back, set the old value, which `settings_get` showed. Change them only when the person asks.
