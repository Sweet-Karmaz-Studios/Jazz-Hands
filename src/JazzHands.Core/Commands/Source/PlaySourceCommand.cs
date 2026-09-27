namespace JazzHands.Core.Commands;

/// <summary>Plays the source monitor from its playhead, or pauses it.</summary>
/// <param name="Play">True to play, false to pause; toggles when left out.</param>
[Command("source.play",
    Description = "Play or pause the source monitor",
    Undoable = false,
    NotUndoableReason = "Playback moves the source playhead. It does not change the project.")]
public sealed record PlaySourceCommand(
    [property: Option("play", "True to play, false to pause; toggles when left out")] bool? Play = null) : ICommand;
