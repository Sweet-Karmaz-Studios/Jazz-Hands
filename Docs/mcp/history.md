# Undo and history

Every command that changes the project is one step in a single undo history shared by everyone working on it: the person at the editor, you, and anything attached with `jazz`. `undo` takes back the latest step, whoever made it, and `redo` puts it back; both take `steps` for several.

`history` lists the steps, oldest first, with who made each: `gui` for the person, `rpc:mcp` for you when attached (`mcp` when you opened the project yourself), `rpc:cli` for the jazz command line, `console` for the editor's Command Console. Before undoing, check the latest step is yours; undoing the person's work is rarely what they want.

`apply_batch` without references is one step however many commands it holds, which makes a multi-part change easy to take back whole.
