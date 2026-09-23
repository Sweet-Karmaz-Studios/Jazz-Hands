namespace JazzHands.Core.Commands;

/// <summary>Stops where the playhead is.</summary>
[Command("playback.pause",
    Description = "Pause where the playhead is",
    Undoable = false,
    NotUndoableReason = "Playback moves the playhead. It does not change the project.")]
public sealed record PauseCommand : ICommand;
