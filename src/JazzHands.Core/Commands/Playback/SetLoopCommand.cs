namespace JazzHands.Core.Commands;

/// <summary>Loops playback over the in and out points, or the whole sequence when there are none.</summary>
/// <param name="On">On or off. Leave it out to flip whatever it is now.</param>
[Command("playback.loop",
    Description = "Loop playback over the in and out points",
    Undoable = false,
    NotUndoableReason = "Looping is a setting of the editor, not of the project.")]
public sealed record SetLoopCommand(
    [property: Arg(0, "on or off; leave it out to flip it")] bool? On = null) : ICommand;
