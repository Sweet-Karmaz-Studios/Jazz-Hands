namespace JazzHands.Core.Commands;

/// <summary>Writes the project to disk.</summary>
/// <param name="Path">Where to write it. Defaults to where it came from.</param>
[Command("project.save",
    Description = "Save the project, atomically",
    Undoable = false,
    NotUndoableReason = "It writes a file. Undoing it would mean deleting the user's save.")]
public sealed record SaveProjectCommand(
    [property: Arg(0, "Where to save it, if not where it came from")] string? Path = null) : ICommand;
