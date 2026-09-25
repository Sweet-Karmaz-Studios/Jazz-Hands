using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Measures a clip, a track or the whole mix on its own: the highest sample, the RMS and the
/// integrated loudness, as <c>audio.normalize</c> measures them.
/// </summary>
/// <remarks>
/// A clip is heard alone with its effects and volume, before its track's; a video clip stands for
/// its linked sound. A track is heard alone with its effects and fader, before the master. The mix
/// is every track through the master volume, without the limiter. Mixed offline through the graph
/// playback uses, over the clips' span, the track's or the sequence's.
/// </remarks>
/// <param name="ClipId">A clip, or its linked sound for a video clip.</param>
/// <param name="TrackId">A track.</param>
/// <param name="Mix">The whole mix.</param>
/// <param name="SequenceId">For <c>--mix</c>, which sequence; the active one when not given.</param>
[Query("audio.measure", Description = "Measure a clip, a track or the mix on its own: peak, RMS and integrated loudness (LUFS)")]
public sealed record MeasureAudioQuery(
    [property: Option("clip", "A clip, or its linked sound for a video clip")] string? ClipId = null,
    [property: Option("track", "A track")] string? TrackId = null,
    [property: Option("mix", "The whole mix, before the limiter")] bool Mix = false,
    [property: Option("sequence", "For --mix, which sequence")] string? SequenceId = null) : IQuery<AudioMeasurement>;

/// <summary>What <c>audio.measure</c> heard.</summary>
/// <param name="What">What was measured: the clips' ids, the track's id, or "mix".</param>
/// <param name="From">Where on the sequence it started.</param>
/// <param name="To">Where it stopped.</param>
/// <param name="PeakDb">The highest sample in dBFS; null for silence.</param>
/// <param name="RmsDb">The RMS in dBFS; null for silence.</param>
/// <param name="Lufs">Integrated loudness (EBU R128); null when everything is under the gate.</param>
public sealed record AudioMeasurement(string What, Flicks From, Flicks To, double? PeakDb, double? RmsDb, double? Lufs);
