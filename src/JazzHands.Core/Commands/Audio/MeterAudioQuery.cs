using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What the meters read over a stretch of a sequence's mix: peak, RMS, true peak and loudness.</summary>
/// <remarks>
/// Mixes the stretch offline through the same graph playback uses, master fader and limiter
/// included, and reads the master's meter and every track's as the Mixer panel would. Loudness is
/// EBU R128: momentary over the last 400 ms, short-term over the last 3 s, integrated gated over
/// the whole stretch. The meter starts at the stretch's start, so to read the short-term loudness
/// at a moment as playback would, start at least 3 s before it.
/// </remarks>
/// <param name="From">Where to start, on the sequence. The start when not given.</param>
/// <param name="To">Where to stop. The end of the last clip when not given.</param>
/// <param name="SequenceId">Which sequence; the active one when not given.</param>
[Query("audio.meter", Description = "Meter a stretch of the mix: peak, true peak, RMS and LUFS")]
public sealed record MeterAudioQuery(
    [property: Option("from", "Where to start on the sequence")] Flicks? From = null,
    [property: Option("to", "Where to stop")] Flicks? To = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : IQuery<AudioMeterSummary>;
