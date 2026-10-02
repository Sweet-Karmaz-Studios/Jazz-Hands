using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Cuts a clip in two at a timeline time.</summary>
/// <remarks>
/// The clips linked to it (its sound, or its picture) that run across the same moment are cut
/// too, unless <paramref name="Alone"/>; the right pieces are then linked to each other, so each
/// half moves with its own sound. One undo step.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="At">Where to cut, on the timeline.</param>
/// <param name="NewClipId">The identifier for the right half. A fresh one when left out.</param>
/// <param name="Alone">Cut only this clip, not the clips linked to it.</param>
[Command("clip.split", Description = "Cut a clip in two at a timeline time")]
public sealed record SplitClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("at", "Where to cut, on the timeline")] Flicks At,
    [property: Option("id", "The identifier for the right half")] string? NewClipId = null,
    [property: Option("alone", "Cut only this clip, not the clips linked to it")] bool Alone = false) : ICommand;
