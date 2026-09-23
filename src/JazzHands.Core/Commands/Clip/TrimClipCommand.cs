using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves a clip start or end without moving the other.</summary>
/// <remarks>
/// Trimming consumes or releases source material: dragging the front edge right shows less of the
/// source and starts later. With ripple on, everything after the clip moves by the same amount.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="In">Where the clip should start, on the timeline.</param>
/// <param name="Out">Where the clip should end, on the timeline.</param>
/// <param name="Ripple">Move everything after it by the same amount.</param>
[Command("clip.trim", Description = "Move a clip start or end")]
public sealed record TrimClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("in", "Where the clip should start")] Flicks? In = null,
    [property: Option("out", "Where the clip should end")] Flicks? Out = null,
    [property: Option("ripple", "Move everything after it too")] bool Ripple = false) : ICommand;
