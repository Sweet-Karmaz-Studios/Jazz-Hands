namespace JazzHands.Core.Commands;

/// <summary>
/// Brings back the work lost when the editor closed without saving: the saved project with every
/// command since replayed, or the autosave copy with the commands after it.
/// </summary>
/// <param name="File">A never saved project a crash rescued, from <c>recovery.check</c>, instead of this project's own.</param>
[Command("recovery.accept",
    Description = "Bring back unsaved work after a crash: the saved project with every command since replayed",
    Undoable = false,
    NotUndoableReason = "It replaces the project as opening one does; the file itself is untouched until you save.")]
public sealed record AcceptRecoveryCommand(
    [property: Option("file", "A rescued untitled project from recovery.check, instead of this project's own")] string? File = null) : ICommand;
