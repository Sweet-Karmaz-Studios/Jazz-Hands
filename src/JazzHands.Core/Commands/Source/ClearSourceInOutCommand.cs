namespace JazzHands.Core.Commands;

/// <summary>Clears the source's in and out marks.</summary>
[Command("source.clear-in-out",
    Description = "Clear the source's marks",
    Undoable = false,
    NotUndoableReason = "The source marks are the viewer's, not the project's.")]
public sealed record ClearSourceInOutCommand : ICommand;
