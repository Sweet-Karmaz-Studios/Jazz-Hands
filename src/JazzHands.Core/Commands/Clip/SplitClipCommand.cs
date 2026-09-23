using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Cuts a clip in two at a timeline time.</summary>
/// <param name="ClipId">Which clip.</param>
/// <param name="At">Where to cut, on the timeline.</param>
/// <param name="NewClipId">The identifier for the right half. A fresh one when left out.</param>
[Command("clip.split", Description = "Cut a clip in two at a timeline time")]
public sealed record SplitClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("at", "Where to cut, on the timeline")] Flicks At,
    [property: Option("id", "The identifier for the right half")] string? NewClipId = null) : ICommand;
