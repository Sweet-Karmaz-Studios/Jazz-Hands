using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Moves clips earlier or later by whole frames, together.</summary>
/// <remarks>
/// What the comma and full stop keys do to a selection. All or nothing: if any clip would land
/// before zero, on a locked track or on top of a clip that is not moving with it, none move. The
/// clips keep their tracks and their distance from each other. Frames are the sequence's.
/// </remarks>
/// <param name="ClipIds">The clips to move, all in one sequence.</param>
/// <param name="Frames">How far: positive is later, negative earlier.</param>
[Command("clip.nudge", Description = "Move clips earlier or later by whole frames")]
public sealed record NudgeClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds,
    [property: Option("frames", "How many frames; negative moves earlier")] int Frames = 1) : ICommand;
