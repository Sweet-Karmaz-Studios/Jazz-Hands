using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>A speed ramp: from one speed to another over a stretch of the clip, in one step.</summary>
/// <remarks>
/// Turns time remapping on if it is off, then puts two keyframes on the clip's <c>remap</c>
/// curve: the starting speed at <paramref name="At"/> and the ending speed at the end of
/// <paramref name="Duration"/>, eased unless told to be linear. Keyframes already inside that
/// stretch are replaced; the rest of the curve stays. Times are on the sequence, like
/// <c>keyframe.add --at</c>.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="From">The speed the ramp starts at.</param>
/// <param name="To">The speed it ends at.</param>
/// <param name="At">When it starts, on the sequence.</param>
/// <param name="Duration">How long it takes.</param>
/// <param name="Linear">Change speed at a constant rate rather than easing in and out.</param>
[Command("clip.ramp-speed", Description = "Ramp a clip from one speed to another")]
public sealed record RampClipSpeedCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("from", "The speed it starts at: 1 is normal")] double From,
    [property: Option("to", "The speed it ends at")] double To,
    [property: Option("at", "When the ramp starts, on the sequence")] Flicks At,
    [property: Option("dur", "How long the ramp takes")] Flicks Duration,
    [property: Option("linear", "A constant change rather than an ease")] bool Linear = false) : ICommand;
