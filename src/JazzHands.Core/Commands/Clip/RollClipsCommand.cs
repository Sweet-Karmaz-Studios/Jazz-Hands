using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves the cut between two touching clips, keeping both their positions.</summary>
/// <remarks>One clip gets longer by exactly what the other loses, so nothing after them moves.</remarks>
/// <param name="LeftClipId">The outgoing clip.</param>
/// <param name="RightClipId">The incoming clip.</param>
/// <param name="By">How far to move the cut. Negative moves it earlier.</param>
[Command("clip.roll", Description = "Move the cut between two touching clips")]
public sealed record RollClipsCommand(
    [property: Arg(0, "The outgoing clip id")] string LeftClipId,
    [property: Arg(1, "The incoming clip id")] string RightClipId,
    [property: Option("by", "How far to move the cut")] Flicks By) : ICommand;
