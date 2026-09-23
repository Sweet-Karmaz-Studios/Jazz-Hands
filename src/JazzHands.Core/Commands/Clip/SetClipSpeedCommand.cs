using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Changes how fast a clip plays.</summary>
/// <remarks>
/// The rate is exact, so 1/2 is half speed and stays half speed however long the timeline gets.
/// By default the clip keeps its source range and its timeline duration changes to match; with
/// <c>keep-duration</c> it keeps its place on the timeline and shows more or less of the source.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="Speed">The rate, as a ratio: 2 for double speed, 1/2 for half.</param>
/// <param name="KeepDuration">Keep the timeline duration and change what is shown instead.</param>
[Command("clip.set-speed", Description = "Change how fast a clip plays")]
public sealed record SetClipSpeedCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("speed", "The rate, for example 2 or 1/2")] Rational Speed,
    [property: Option("keep-duration", "Keep the timeline duration")] bool KeepDuration = false) : ICommand;
