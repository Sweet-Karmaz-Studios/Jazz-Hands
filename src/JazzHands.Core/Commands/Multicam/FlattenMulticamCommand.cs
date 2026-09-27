namespace JazzHands.Core.Commands;

/// <summary>Turns a multicam clip into ordinary cuts: each angle's stretch as a clip of its own.</summary>
/// <remarks>
/// The picture of each stretch goes on the multicam clip's track and its sound on audio tracks,
/// lowest first, linked; the multicam sequence goes when nothing else uses it. One undo.
/// </remarks>
/// <param name="ClipId">The multicam clip.</param>
[Command("multicam.flatten", Description = "Turn a multicam clip into ordinary cuts")]
public sealed record FlattenMulticamCommand(
    [property: Arg(0, "The multicam clip id")] string ClipId) : ICommand;
