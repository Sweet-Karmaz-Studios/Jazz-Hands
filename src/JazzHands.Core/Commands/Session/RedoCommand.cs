namespace JazzHands.Core.Commands;

/// <summary>Puts back a command that was taken back.</summary>
/// <param name="Steps">How many commands to put back.</param>
[Command("redo",
    Description = "Put back a command that was undone",
    Undoable = false,
    NotUndoableReason = "Undo is what takes a redo back.")]
public sealed record RedoCommand(
    [property: Option("steps", "How many commands to put back")] int Steps = 1) : ICommand;
