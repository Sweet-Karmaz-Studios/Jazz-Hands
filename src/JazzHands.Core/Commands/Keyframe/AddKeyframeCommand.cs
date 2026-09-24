using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Adds a keyframe to a parameter, or changes the one already at that time.
/// </summary>
/// <remarks>
/// The first keyframe on a parameter turns animation on: its constant becomes a curve. Without a
/// value the keyframe takes what the parameter is worth at that time, which is what pressing the
/// stopwatch or the diamond in the inspector does. The owner is any clip, track, effect or mask;
/// <c>jazz param list &lt;id&gt;</c> shows what it has.
/// </remarks>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name.</param>
/// <param name="At">When, on the sequence.</param>
/// <param name="Value">The value, as text; what the parameter is worth there when not given.</param>
/// <param name="Interp">How the curve leaves this keyframe; linear, or the neighbour's, when not given.</param>
/// <param name="Local">Read the time as relative to the clip's start rather than the sequence's.</param>
[Command("keyframe.add", Description = "Add a keyframe to a parameter")]
public sealed record AddKeyframeCommand(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Option("at", "When, on the sequence")] Flicks At,
    [property: Option("value", "The value; what it is worth there when not given")] string? Value = null,
    [property: Option("interp", "hold, linear, bezier, ease-in, ease-out or ease-in-out")] Interp? Interp = null,
    [property: Option("local", "Read --at from the clip's start rather than the sequence's")] bool Local = false) : ICommand;
