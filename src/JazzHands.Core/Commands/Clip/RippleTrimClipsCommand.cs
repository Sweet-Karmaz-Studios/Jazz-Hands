using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves one edge of clips and ripples everything after it along every sync-locked track.</summary>
/// <remarks>
/// A start edge keeps the clip where it begins and takes source off (or puts it back on) its
/// front; an end edge moves the end. Either way everything after the edge moves by the same
/// amount on the clips' tracks and on every sync-locked track, so nothing downstream drifts out of
/// sync. Several clips trim together only when they share the edge, as a picture and its sound do;
/// the clips linked to the ones given are trimmed with them unless <paramref name="Alone"/>.
/// </remarks>
/// <param name="ClipIds">The clips, all sharing the edge.</param>
/// <param name="Edge">Which edge: start or end.</param>
/// <param name="To">Where the edge goes, on the timeline.</param>
/// <param name="Alone">Trim only these clips, not the clips linked to them.</param>
[Command("clip.ripple-trim", Description = "Trim a clip edge and ripple everything after it")]
public sealed record RippleTrimClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds,
    [property: Option("edge", "start or end")] ClipEdge Edge,
    [property: Option("to", "Where the edge goes")] Flicks To,
    [property: Option("alone", "Only these clips, not the clips linked to them")] bool Alone = false) : ICommand;
