using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Sets any parameter of a clip, track, effect or mask.
/// </summary>
/// <remarks>
/// A parameter without keyframes becomes this constant. One with keyframes needs <c>--at</c>,
/// and gets a keyframe there, new or changed; without it the command is refused rather than
/// throwing the animation away, which is what <c>param.clear-keyframes</c> is for. Setting a
/// parameter to its default stores nothing.
/// </remarks>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name: transform.position, opacity, volume, or an effect's own.</param>
/// <param name="Value">The value, as text: 12, "100, 50", "#FF8800", both.</param>
/// <param name="At">For a keyframed parameter, the keyframe's time on the sequence.</param>
/// <param name="Local">Read the time as relative to the clip's start rather than the sequence's.</param>
[Command("param.set", Description = "Set a parameter of a clip, track, effect or mask")]
public sealed record SetParamCommand(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name, for example transform.position or radius")] string Param,
    [property: Arg(2, "The value, for example 12, '100, 50' or #FF8800")] string Value,
    [property: Option("at", "For a keyframed parameter, the keyframe's time on the sequence")] Flicks? At = null,
    [property: Option("local", "Read --at from the clip's start rather than the sequence's")] bool Local = false) : IMergeableCommand
{
    /// <inheritdoc />
    public bool Continues(ICommand previous) =>
        previous is SetParamCommand before && before.OwnerId == OwnerId && before.Param == Param && before.At == At && before.Local == Local;
}
