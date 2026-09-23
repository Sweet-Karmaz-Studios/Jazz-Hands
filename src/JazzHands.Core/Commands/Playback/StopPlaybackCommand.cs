namespace JazzHands.Core.Commands;

/// <summary>Stops, and puts the playhead back where playback last started.</summary>
[Command("playback.stop",
    Description = "Stop and return to where playback started",
    Undoable = false,
    NotUndoableReason = "Playback moves the playhead. It does not change the project.")]
public sealed record StopPlaybackCommand : ICommand;
