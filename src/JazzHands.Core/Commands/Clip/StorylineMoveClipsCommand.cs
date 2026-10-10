using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves clips along a magnetic storyline: out, close up, and back in at the nearest cut.</summary>
/// <remarks>
/// The clips on the primary picture track go end to end, in their order, at the cut nearest
/// <c>--to</c>, pushing what is after it on; nothing lands on top of anything and no gap is left.
/// Clips on other tracks that start over one of them, such as its sound, go with it and keep
/// their place against it. With none on the primary track, pictures from another track join it
/// there, leaving a gap where they were. Works whether or not the sequence is magnetic.
/// </remarks>
/// <param name="ClipIds">The clips: at least one on the primary picture track, or pictures to put on it.</param>
/// <param name="To">Where the first of them was dropped.</param>
[Command("clip.storyline-move", Description = "Move clips along the primary track, closing up behind them")]
public sealed record StorylineMoveClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds,
    [property: Option("to", "Where the first of them was dropped")] Flicks To) : ICommand;
