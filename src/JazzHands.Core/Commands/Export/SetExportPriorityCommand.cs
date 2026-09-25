using JazzHands.Core.Export;

namespace JazzHands.Core.Commands;

/// <summary>Moves an export that has not started up or down the queue.</summary>
/// <param name="JobId">The job.</param>
/// <param name="Priority">low, normal or high.</param>
[Command("export.set-priority",
    Description = "Move an export up or down the queue",
    Undoable = false,
    NotUndoableReason = "The export queue is not part of the project.")]
public sealed record SetExportPriorityCommand(
    [property: Arg(0, "The job id")] string JobId,
    [property: Arg(1, "low, normal or high")] ExportPriority Priority) : ICommand;
