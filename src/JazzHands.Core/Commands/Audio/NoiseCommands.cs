using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Learns a clip's background noise from a stretch with nothing else in it, and takes it out with
/// noise reduction.
/// </summary>
/// <remarks>
/// <para>
/// Measures the noise's spectrum over <c>--from</c> to <c>--to</c> (sequence times inside the
/// clip), or over the clip's quietest half second when they are not given, and puts it on the
/// clip's <c>audio.denoise</c> effect, adding one if it has none. A video clip stands for the
/// sound linked to it, each stream learning its own noise over the same stretch. The clip plays at
/// normal speed.
/// </para>
/// <para>
/// <c>audio.reduce-noise</c> changes how much is taken out or takes it off. Bypass the effect in the
/// Inspector to compare. One undo.
/// </para>
/// </remarks>
/// <param name="ClipId">A sound clip, or a video clip for its linked sound.</param>
/// <param name="From">Where the stretch of noise starts, on the sequence.</param>
/// <param name="To">Where it ends.</param>
[Command("audio.learn-noise", Description = "Learn a clip's background noise (a fan, hiss, the room) from a stretch with only noise in it, and take it out with noise reduction")]
public sealed record LearnNoiseCommand(
    [property: Arg(0, "A sound clip, or a video clip for its linked sound")] string ClipId,
    [property: Option("from", "Where the stretch of noise starts, on the sequence. Default: the clip's quietest half second")] Flicks? From = null,
    [property: Option("to", "Where it ends")] Flicks? To = null) : ICommand;

/// <summary>Turns noise reduction on a clip on, changes how much it takes out, or takes it off.</summary>
/// <remarks>
/// Adds an <c>audio.denoise</c> effect when the clip has none, which without a learned profile
/// follows each frequency's quietest level by itself; <c>audio.learn-noise</c> gives it the noise
/// to take out. A video clip stands for its linked sound. One undo.
/// </remarks>
/// <param name="ClipId">A sound clip, or a video clip for its linked sound.</param>
/// <param name="Reduction">How far the noise is turned down at most, in dB.</param>
/// <param name="Sensitivity">How far over the noise a sound must be to be kept.</param>
/// <param name="Off">Take noise reduction off.</param>
[Command("audio.reduce-noise", Description = "Turn a clip's noise reduction on, change how much it takes out, or take it off")]
public sealed record ReduceNoiseCommand(
    [property: Arg(0, "A sound clip, or a video clip for its linked sound")] string ClipId,
    [property: Option("reduction", "How far the noise goes down at most: 12dB. Default: 12")] double? Reduction = null,
    [property: Option("sensitivity", "How far over the noise a sound must be to stay, 0.5 to 4. Default: 2")] double? Sensitivity = null,
    [property: Option("off", "Take noise reduction off")] bool Off = false) : ICommand;
