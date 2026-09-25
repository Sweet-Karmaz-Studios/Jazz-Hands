# Chapters

Chapters are markers flagged to be written into exported files, so a player shows them in its chapter list and YouTube reads them from the description. Each runs from its mark to the next one.

- `chapter_add` puts a chapter mark at a time with a title.
- `chapter_from_markers` makes every marker on a sequence a chapter.
- `chapter_import` brings a media file's own chapters in as marks, placed where the file starts on the timeline.
- `chapter_list` shows them in order.

Exports write chapters when the container allows it (MP4 and Matroska), unless `chapters: false`.
