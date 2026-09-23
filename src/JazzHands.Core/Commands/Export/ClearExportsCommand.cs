namespace JazzHands.Core.Commands;

/// <summary>Takes finished jobs (done, failed or cancelled) off the export queue. Their files stay.</summary>
[Command("export.clear",
    Description = "Remove finished jobs from the export queue",
    Undoable = false,
    NotUndoableReason = "The export queue is not part of the project.")]
public sealed record ClearExportsCommand : ICommand;
