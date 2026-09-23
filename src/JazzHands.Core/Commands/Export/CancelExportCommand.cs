namespace JazzHands.Core.Commands;

/// <summary>Stops a queued or running export. The partial file is deleted.</summary>
/// <param name="JobId">The job.</param>
[Command("export.cancel",
    Description = "Stop a queued or running export",
    Undoable = false,
    NotUndoableReason = "The export queue is not part of the project.")]
public sealed record CancelExportCommand(
    [property: Arg(0, "The job id")] string JobId) : ICommand;
