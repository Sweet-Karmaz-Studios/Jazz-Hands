using System.Collections.Immutable;
using JazzHands.Core.Model;

namespace JazzHands.Core.Serialization;

/// <summary>
/// Puts a project into the order the rest of the code assumes.
/// </summary>
/// <remarks>
/// The model has invariants that the editing operations maintain and a file cannot be trusted to:
/// clips on a track are in start order, tracks in a sequence are in stacking order, markers are in
/// time order. <see cref="Queries.TimelineQueries.ClipAt"/> binary searches the clips and
/// <see cref="Track.Duration"/> reads the last one, so a hand-edited file that lists them in
/// another order would not fail, which is worse: it would answer questions wrongly.
///
/// Normalizing on load rather than validating and refusing is the right trade. A person editing
/// JSON has no reason to think order matters, and rearranging a list is exactly the kind of thing
/// a tool should do for them. <c>jazz fmt</c> writes the result back.
///
/// Sorting is stable, so two clips at the same start keep the order the file gave them.
/// </remarks>
public static class ProjectNormalizer
{
    /// <summary>Returns the project with every list in its canonical order.</summary>
    public static Project Normalize(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.Sequences.IsEmpty)
        {
            return project;
        }

        var sequences = ImmutableArray.CreateBuilder<Sequence>(project.Sequences.Length);
        foreach (Sequence sequence in project.Sequences)
        {
            sequences.Add(Normalize(sequence));
        }

        return project with { Sequences = new EquatableArray<Sequence>(sequences.ToImmutable()) };
    }

    /// <summary>Returns the sequence with its tracks, clips and markers in order.</summary>
    public static Sequence Normalize(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var tracks = ImmutableArray.CreateBuilder<Track>(sequence.Tracks.Length);
        foreach (Track track in sequence.Tracks.OrderBy(track => track.Order))
        {
            tracks.Add(Normalize(track));
        }

        return sequence with
        {
            Tracks = new EquatableArray<Track>(tracks.ToImmutable()),
            Markers = SortMarkers(sequence.Markers),
        };
    }

    /// <summary>Returns the track with its clips in start order.</summary>
    public static Track Normalize(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Clips.IsEmpty)
        {
            return track;
        }

        var clips = ImmutableArray.CreateBuilder<Clip>(track.Clips.Length);
        foreach (Clip clip in track.Clips.OrderBy(clip => clip.Start))
        {
            clips.Add(clip.Markers.IsEmpty ? clip : clip with { Markers = SortMarkers(clip.Markers) });
        }

        return track with { Clips = new EquatableArray<Clip>(clips.ToImmutable()) };
    }

    private static EquatableArray<Marker> SortMarkers(EquatableArray<Marker> markers) =>
        markers.IsEmpty
            ? markers
            : new EquatableArray<Marker>(markers.OrderBy(marker => marker.Time).ToImmutableArray());
}
