namespace JazzHands.Core.Commands;

/// <summary>Shows the editor's window and brings it to the front.</summary>
/// <remarks>
/// Like every <c>app</c> command, this needs a running editor: a headless process has no window,
/// and says so with the code <c>no-app</c>.
/// </remarks>
[Command("app.show",
    Description = "Show the editor's window and bring it to the front",
    Undoable = false,
    NotUndoableReason = "It changes the window, not the project.")]
public sealed record ShowAppCommand : ICommand;
