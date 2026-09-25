namespace JazzHands.Core.Commands;

/// <summary>
/// Declines the recovery: the autosave copy and the command history are moved into the sidecar's
/// recovered folder, where the last few are kept, and the project is as it was saved.
/// </summary>
/// <param name="File">A rescued untitled project to remove instead.</param>
[Command("recovery.discard",
    Description = "Decline the recovery: set the autosave copy and command history aside and keep the project as saved",
    Undoable = false,
    NotUndoableReason = "It moves the recovery files aside; they are kept in the project's .jazz.d/recovered folder.")]
public sealed record DiscardRecoveryCommand(
    [property: Option("file", "A rescued untitled project to remove instead")] string? File = null) : ICommand;
