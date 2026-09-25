# Export

An export writes a sequence (or a stretch of it) to a file through a preset: `list_presets` names them (YouTube 1080p and 4K, Discord size targets, proof, lossless, ProRes and more). `export_enqueue` queues one; the answer's `changedIds` holds the job id, and `wait_export` waits for it with progress. In the editor, exports run on the queue while editing goes on; headless, they run before the tool returns.

`render_proof` is the quick one: a 480p proof of the whole sequence or a stretch, waited for. Use it to watch a cut.

Mode: `auto` chooses. A timeline that is one file played untouched can be copied without re-encoding (`copy`, cuts on keyframes) or smart cut (`smart`, exact cuts with only the frames near each cut encoded again); anything with effects, titles or more than one source is encoded (`encode`). `export_plan` shows what would happen, and why, without doing it.

Overrides on `export_enqueue`: `size`, `frameRate`, `quality` or `bitrate`, `encoders`, sound encoder and bitrate, `channels`, `loudness` (for example -14 LUFS), `targetSize` (such as `8MB`), subtitles and chapters, and `start` and `end` or `useInOut` for a stretch.

The queue: `export_list`, `export_log`, `export_pause`, `export_resume`, `export_cancel`, `export_set_priority`, `export_clear`. `export_still` writes one frame as the export would draw it; `export_contact_sheet` writes a sheet to a file (`contact_sheet` returns one as an image instead). Relative output paths are beside the project.
