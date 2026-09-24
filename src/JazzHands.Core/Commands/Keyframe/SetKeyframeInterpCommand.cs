using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Changes how the curve leaves a keyframe: held, straight, eased or a free bezier.</summary>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name.</param>
/// <param name="At">The keyframe's time on the sequence.</param>
/// <param name="Interp">The new shape.</param>
/// <param name="Local">Read the time as relative to the clip's start rather than the sequence's.</param>
[Command("keyframe.set-interp", Description = "Change how the curve leaves a keyframe")]
public sealed record SetKeyframeInterpCommand(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Option("at", "The keyframe's time on the sequence")] Flicks At,
    [property: Option("interp", "hold, linear, bezier, ease-in, ease-out or ease-in-out")] Interp Interp,
    [property: Option("local", "Read --at from the clip's start rather than the sequence's")] bool Local = false) : ICommand;
