namespace JazzHands.Core.Commands;

/// <summary>What <c>audio.normalize</c> measures.</summary>
public enum NormalizeMode
{
    /// <summary>The highest sample, in dBFS.</summary>
    Peak,

    /// <summary>The average power, in dBFS.</summary>
    Rms,

    /// <summary>Integrated loudness, EBU R128, in LUFS.</summary>
    Lufs,
}

/// <summary>
/// Sets the gain of a clip, a track or the whole mix so that it measures a target level: its peak,
/// its RMS or its integrated loudness.
/// </summary>
/// <remarks>
/// <para>
/// Measures what is asked for on its own, through the graph playback uses, then moves its volume
/// by the difference: a clip's volume (with its effects, before its track), a track's fader (with
/// its effects, before the master), or the master volume for <c>--mix</c> (with the master limiter
/// left out of the measurement). A video clip stands for the sound linked to it, which moves
/// together by one gain. Keyframed volume moves as a whole, so its shape is kept.
/// </para>
/// <para>
/// Targets when none is given: -1 dBFS peak, -20 dBFS RMS, -14 LUFS (YouTube and Spotify; -16 for
/// Apple, -23 for broadcast). Silence, a driven volume and a gain beyond +24 dB are refused.
/// <c>audio.measure</c> reads the same measurement without changing anything, and an export's
/// <c>--loudness</c> normalises the export alone. One undo.
/// </para>
/// </remarks>
/// <param name="ClipId">A clip: its volume, or its linked sound's for a video clip.</param>
/// <param name="TrackId">A track: its fader.</param>
/// <param name="Mix">The whole mix: the master volume.</param>
/// <param name="Mode">What to measure.</param>
/// <param name="Target">The level to reach: dBFS for peak and RMS, LUFS for loudness.</param>
/// <param name="SequenceId">For <c>--mix</c>, which sequence; the active one when not given.</param>
[Command("audio.normalize", Description = "Set a clip's, a track's or the mix's gain so it measures a peak, RMS or loudness (LUFS) target")]
public sealed record NormalizeAudioCommand(
    [property: Option("clip", "A clip: its volume, or its linked sound's for a video clip")] string? ClipId = null,
    [property: Option("track", "A track: its fader")] string? TrackId = null,
    [property: Option("mix", "The whole mix, by the master volume")] bool Mix = false,
    [property: Option("mode", "peak, rms or lufs. Default: lufs")] NormalizeMode Mode = NormalizeMode.Lufs,
    [property: Option("target", "dBFS for peak and rms, LUFS for lufs. Default: -1, -20 and -14")] double? Target = null,
    [property: Option("sequence", "For --mix, which sequence")] string? SequenceId = null) : ICommand
{
    /// <summary>The target a mode normalises to when none is given.</summary>
    public static double DefaultTarget(NormalizeMode mode) => mode switch
    {
        NormalizeMode.Peak => -1.0,
        NormalizeMode.Rms => -20.0,
        _ => -14.0,
    };
}
