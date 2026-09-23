namespace JazzHands.Core.Commands;

/// <summary>Takes back the last command.</summary>
/// <remarks>
/// Handled by the dispatcher rather than by a handler, because it restores a snapshot rather than
/// computing a new project. It is not itself undoable; redo is what takes it back.
/// </remarks>
/// <param name="Steps">How many commands to take back.</param>
[Command("undo",
    Description = "Take back the last command",
    Undoable = false,
    NotUndoableReason = "Redo is what takes an undo back.")]
public sealed record UndoCommand(
    [property: Option("steps", "How many commands to take back")] int Steps = 1) : ICommand;
