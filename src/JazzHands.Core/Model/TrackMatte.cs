namespace JazzHands.Core.Model;

/// <summary>What of the matte track's picture a clip keeps.</summary>
public enum TrackMatteMode
{
    /// <summary>Where the matte is opaque: footage inside text.</summary>
    Alpha,

    /// <summary>Where the matte is bright: a reveal through a white shape on black.</summary>
    Luma,

    /// <summary>Where the matte is transparent: footage around text.</summary>
    AlphaInverted,

    /// <summary>Where the matte is dark.</summary>
    LumaInverted,
}

/// <summary>
/// Another track used as a clip's (or a whole track's) matte: the clip shows only where that
/// track's picture is opaque, or bright, or the opposite.
/// </summary>
/// <remarks>
/// The matte track is drawn on its own at the same moment, with its own clips, effects,
/// transitions and animation, and hidden from the output, as Premiere's Track Matte Key does, so a
/// title on it cuts the gameplay under it without being seen itself. Luma is the matte's light,
/// in linear light; alpha is its coverage. A clip's own masks still apply, inside the matte.
/// </remarks>
/// <param name="SourceTrackId">The video track whose picture is the matte.</param>
/// <param name="Mode">What of it the clip keeps.</param>
public sealed record TrackMatte(string SourceTrackId, TrackMatteMode Mode = TrackMatteMode.Alpha) : IEquatable<TrackMatte>
{
    /// <summary>What applies to a clip: its own, else its track's; null for none.</summary>
    public static TrackMatte? For(Clip clip, Track track)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(track);
        return clip.Matte ?? track.Matte;
    }

    /// <summary>The tracks of a sequence something uses as a matte, which are hidden from its output.</summary>
    public static HashSet<string> Sources(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (Track track in sequence.Tracks)
        {
            if (track.Matte is { } own)
            {
                sources.Add(own.SourceTrackId);
            }

            foreach (Clip clip in track.Clips)
            {
                if (clip.Matte is { } matte)
                {
                    sources.Add(matte.SourceTrackId);
                }
            }
        }

        return sources;
    }
}
