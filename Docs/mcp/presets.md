# Export presets

A preset is everything an export needs to know about the file it writes: container, encoders in order of preference, size, frame rate, quality or bitrate, sound, loudness and size target. The built-in ones are `youtube-1080p`, `youtube-1440p`, `youtube-4k`, `youtube-4k-av1`, `web-vp9`, `discord-8mb`, `discord-25mb`, `discord-50mb`, `gif-discord`, `shield-direct`, `fire-direct`, `proof`, `lossless`, `archive-prores`, `archive-dnxhr`, `png-sequence`, `audio-only`, `audio-wav` and `audio-mp3`.

`list_presets` lists them with a line each; `presets_get` shows one in full. `presets_save` keeps one of your own, starting `from` another with changes, or from JSON; `presets_delete` removes one of yours. Presets live in the person's settings, not the project, so saving one is not undoable. For a one-off change, pass overrides to `export_enqueue` instead.
