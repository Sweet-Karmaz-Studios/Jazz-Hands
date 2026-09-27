using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Silences a clip's sound wherever it plays faster than a speed.</summary>
/// <remarks>
/// For speed ramps: the sound drops out through the fast part and comes back, with a few
/// milliseconds of fade either side so nothing clicks, instead of rushing by. A clip at one speed
/// above the limit is silent throughout. <c>--off</c> takes the setting away.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="Above">The speed above which it is silent: 2 for anything faster than double speed. Default: 2.</param>
/// <param name="Off">Never silence it.</param>
[Command("clip.set-fast-mute", Description = "Silence a clip's sound where it plays faster than a speed")]
public sealed record SetClipFastMuteCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("above", "The speed above which it is silent, for example 2 or 3/2. Default: 2")] Rational? Above = null,
    [property: Option("off", "Never silence it")] bool Off = false) : ICommand;
