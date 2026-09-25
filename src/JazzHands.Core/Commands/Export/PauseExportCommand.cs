namespace JazzHands.Core.Commands;

/// <summary>
/// Holds an export, or every unfinished one when no job is named. A job that has not started waits;
/// one that is running stops, its partial file deleted, and starts again from the top when resumed.
/// </summary>
/// <param name="JobId">The job, or null for all of them.</param>
[Command("export.pause",
    Description = "Hold an export, or all of them",
    Undoable = false,
    NotUndoableReason = "The export queue is not part of the project.")]
public sealed record PauseExportCommand(
    [property: Arg(0, "The job id; every job when left out")] string? JobId = null) : ICommand;
