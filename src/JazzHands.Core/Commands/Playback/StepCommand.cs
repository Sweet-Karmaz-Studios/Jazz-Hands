namespace JazzHands.Core.Commands;

/// <summary>Moves the playhead by whole frames and pauses there.</summary>
/// <param name="Frames">How many frames; negative steps back.</param>
[Command("playback.step",
    Description = "Step the playhead by whole frames",
    Undoable = false,
    NotUndoableReason = "Playback moves the playhead. It does not change the project.")]
public sealed record StepCommand(
    [property: Option("frames", "How many frames; negative steps back")] int Frames = 1) : ICommand;
