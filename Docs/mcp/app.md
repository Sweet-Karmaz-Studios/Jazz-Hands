# The editor's window and process

Jazz Hands keeps running when its window is closed: it sits in the notification area, and the session, the export queue and this connection carry on. These tools reach the window and the process, not the project, so none of them is undoable, and they need the editor (a headless session answers `no-app`).

`app_show` brings the window back, where it was, in front. `app_hide` puts it away to the notification area; the preview lets go of its video memory while hidden, which is kind to a machine someone also games on.

`app_quit` ends the editor. It refuses with `unsaved-changes` when the project is not saved (save it with `project_save` first, or pass `force`), and with `exports-running` while an export runs: pass `waitForExports` to let them finish out of sight and then quit, or `force` to cancel them. The answer comes back before the editor goes, and this connection then closes. Use it at the end of a session only when the person has asked for the editor to be closed.
