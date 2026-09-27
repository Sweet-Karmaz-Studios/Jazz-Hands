using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Grades a clip so its colour matches another's (Phase 44).</summary>
/// <remarks>
/// Looks at one frame of each clip on its own, with nothing above or below it, and solves lift,
/// gamma, gain and saturation on a Colour Wheels effect so the clip's tones and colours spread the
/// way the reference's do. The clip's first Colour Wheels effect is updated, its offset, contrast
/// and pivot kept, and the frame is read with it and the effects after it off; without one, one is
/// added at the end of the chain. One undo step.
/// </remarks>
/// <param name="ClipId">The clip to grade.</param>
/// <param name="ReferenceClipId">The clip whose colour to match.</param>
/// <param name="At">The clip's frame to look at, on the timeline; its middle when not given.</param>
/// <param name="ReferenceAt">The reference's frame to look at, on the timeline; its middle when not given.</param>
[Command("color.match", Description = "Match a clip's colour to another clip")]
public sealed record MatchColorCommand(
    [property: Arg(0, "The clip to grade")] string ClipId,
    [property: Option("to", "The clip whose colour to match")] string ReferenceClipId,
    [property: Option("at", "The clip's frame to look at, on the timeline; its middle when not given")] Flicks? At = null,
    [property: Option("reference-at", "The reference's frame to look at, on the timeline; its middle when not given")] Flicks? ReferenceAt = null) : ICommand;
