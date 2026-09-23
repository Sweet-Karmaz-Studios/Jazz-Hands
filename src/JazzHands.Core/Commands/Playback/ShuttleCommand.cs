namespace JazzHands.Core.Commands;

/// <summary>Plays at a rate other than normal speed, forwards or backwards.</summary>
/// <remarks>
/// Forward rates up to twice normal speed keep their pitch; anything faster, and anything
/// backwards, is silent. Above twice normal speed the picture shows keyframes rather than every
/// frame, which is the trade a shuttle makes. The J and L keys double the rate on each press up
/// to 32.
/// </remarks>
/// <param name="Rate">The rate: 2 is double speed, -1 is normal speed backwards.</param>
[Command("playback.shuttle",
    Description = "Play at a rate, forwards or backwards",
    Undoable = false,
    NotUndoableReason = "Playback moves the playhead. It does not change the project.")]
public sealed record ShuttleCommand(
    [property: Option("rate", "2 is double speed, -1 is backwards")] double Rate) : ICommand;
