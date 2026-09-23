namespace JazzHands.Core.Commands;

/// <summary>Plays when paused and pauses when playing: the space bar.</summary>
[Command("playback.toggle",
    Description = "Play when paused, pause when playing",
    Undoable = false,
    NotUndoableReason = "Playback moves the playhead. It does not change the project.")]
public sealed record TogglePlaybackCommand : ICommand;
