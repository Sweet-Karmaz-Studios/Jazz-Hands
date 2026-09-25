namespace JazzHands.Core.Commands;

/// <summary>Lets a paused export run again, or every paused one when no job is named.</summary>
/// <param name="JobId">The job, or null for all of them.</param>
[Command("export.resume",
    Description = "Let a paused export run again, or all of them",
    Undoable = false,
    NotUndoableReason = "The export queue is not part of the project.")]
public sealed record ResumeExportCommand(
    [property: Arg(0, "The job id; every paused job when left out")] string? JobId = null) : ICommand;
