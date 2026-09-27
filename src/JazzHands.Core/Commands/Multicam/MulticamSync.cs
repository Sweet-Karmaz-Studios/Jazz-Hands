using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>How the recordings of a multicam are lined up (Phase 41).</summary>
public enum MulticamSync
{
    /// <summary>By their sound: the same moment heard in each.</summary>
    Audio,

    /// <summary>By the timecode each recording starts at.</summary>
    Timecode,

    /// <summary>By their in points: each starts where its subclip (or the file) starts.</summary>
    In,

    /// <summary>By the first marker on each file, set on the same moment in each (a clap).</summary>
    Marker,
}

/// <summary>Where each recording of a multicam starts, and how sure the sync is.</summary>
/// <param name="Angles">One per recording, in the order given.</param>
/// <param name="Weakest">The recording whose sync is least sure, when it is by sound; null otherwise.</param>
public sealed record MulticamSyncInfo(EquatableArray<AngleSyncInfo> Angles, string? Weakest);

/// <summary>One recording's place in a multicam.</summary>
/// <param name="MediaId">The media item.</param>
/// <param name="Name">Its name.</param>
/// <param name="Start">Where it starts in the multicam's time.</param>
/// <param name="Confidence">How sure the match is, 0 to 1: 1 for timecode, in points and markers; the correlation for sound.</param>
public sealed record AngleSyncInfo(string MediaId, string Name, Flicks Start, double Confidence);
