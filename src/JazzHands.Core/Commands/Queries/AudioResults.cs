using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What the master's meter read over a stretch of the mix. A level is null where there was only silence.</summary>
/// <param name="From">Where the stretch starts on the sequence.</param>
/// <param name="To">Where it ends.</param>
/// <param name="PeakDbfs">The loudest sample of any channel, dBFS.</param>
/// <param name="TruePeakDbtp">The loudest point between samples, as a converter reconstructs it, dBTP.</param>
/// <param name="RmsDbfs">The RMS of every channel over the whole stretch, dBFS.</param>
/// <param name="MomentaryLufs">Loudness over the last 400 ms of the stretch, LUFS.</param>
/// <param name="ShortTermLufs">Loudness over its last 3 s, LUFS.</param>
/// <param name="MaxShortTermLufs">The loudest 3 s anywhere in it, LUFS.</param>
/// <param name="IntegratedLufs">Gated loudness over the whole stretch, LUFS: the number a delivery specification asks for.</param>
/// <param name="LimiterDb">The most the master limiter turned the mix down, dB (0 or less).</param>
/// <param name="Clipped">True when any sample reached full scale.</param>
/// <param name="Tracks">Each audio track's meter, after its volume and pan.</param>
public sealed record AudioMeterSummary(
    Flicks From,
    Flicks To,
    double? PeakDbfs,
    double? TruePeakDbtp,
    double? RmsDbfs,
    double? MomentaryLufs,
    double? ShortTermLufs,
    double? MaxShortTermLufs,
    double? IntegratedLufs,
    double LimiterDb,
    bool Clipped,
    TrackMeterSummary[] Tracks);

/// <summary>What one track's meter read.</summary>
/// <param name="TrackId">The track.</param>
/// <param name="Name">Its name.</param>
/// <param name="PeakDbfs">Its loudest sample, dBFS; null when it was silent.</param>
/// <param name="RmsDbfs">Its RMS over the stretch, dBFS.</param>
/// <param name="Clipped">True when it reached full scale before the master turned it down.</param>
public sealed record TrackMeterSummary(string TrackId, string Name, double? PeakDbfs, double? RmsDbfs, bool Clipped);
