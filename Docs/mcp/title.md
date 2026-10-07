# Titles

A title is a clip on a video track that draws text: `title_add` places one from a preset (`title-card`, `lower-third`, `end-card`, `caption`, `caption-bold`, `subtitle`; `list_title_presets` shows what each sets). With no `trackId` it goes on the highest video track that is free at that time, adding one when none is.

Text is markup: `[b]bold[/b]`, `[i]italic[/i]`, `[color=#FFCC00]gold[/color]`, `[size=64]big[/size]`, and `\n` for a new line. `title_set_text` changes the words and keeps the look; `plain` takes the text exactly as typed.

`title_set_style` changes the look: font (from `list_fonts`), weight, size in sequence pixels, colour, alignment, position from the frame centre as `"x, y"`, wrap width, outline (`"4 #000000"`), box and shadow. `title_set_animation` chooses how it comes in and goes out: fade, slide-left, slide-right, slide-up, slide-down, scale, typewriter, word-reveal, blur or wipe, each with a duration.

`title_save_preset` keeps a title as a preset of your own (a kebab-case name, a label and a sentence on when to use it): its look, place, text, length and animations, written for a 1080 line frame so it lands the same in any sequence. Presets of your own are files in `%APPDATA%JazzHands	itles`; `replace` writes over one, or puts yours in place of a built-in until the file is deleted. It changes nothing in the project and is not undone.

Check a title by looking at it: `render_frame` at a time inside it. `title_measure` says where its text sits and whether it stays inside title safe, which matters for text near the edges on televisions.
