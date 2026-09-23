namespace JazzHands.Core.Commands;

/// <summary>Selects nothing.</summary>
[Command("selection.clear",
    Description = "Select nothing",
    Undoable = false,
    NotUndoableReason = "The selection is what the editor points at. It is not part of the project.")]
public sealed record ClearSelectionCommand : ICommand;
