using System.Collections.Immutable;
using JazzHands.App.Controls.Timeline;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.App.ViewModels.Timeline;

/// <summary>One clip as the timeline draws it.</summary>
/// <remarks>
/// A thin wrapper over the model's <see cref="Core.Model.Clip"/>, kept from one snapshot to the
/// next while the clip is unchanged, so anything cached against it (the laid out name, later the
/// thumbnail strip) survives an edit somewhere else on the timeline.
/// </remarks>
/// <param name="Clip">The clip.</param>
/// <param name="TrackId">The track it is on.</param>
/// <param name="Kind">The track's kind.</param>
/// <param name="MediaMissing">True when it plays a media item the project no longer has.</param>
public sealed record ClipView(Clip Clip, string TrackId, TrackKind Kind, bool MediaMissing)
{
    /// <summary>The clip's id.</summary>
    public string Id => Clip.Id;

    /// <summary>Where it starts.</summary>
    public Flicks Start => Clip.Start;

    /// <summary>Where it ends, exclusive.</summary>
    public Flicks End => Clip.End;

    /// <summary>What the body shows: the clip's name, and its speed when it is not 1x.</summary>
    public string Label { get; } = Clip.EffectiveSpeed == Rational.One
        ? Clip.Name
        : $"{Clip.Name}  {Clip.EffectiveSpeed.ToDouble() * 100:0.#}%{(Clip.Reverse ? " reversed" : string.Empty)}";
}

/// <summary>One track and its clips, as the timeline draws them.</summary>
/// <param name="Track">The track.</param>
/// <param name="Clips">Its clips, in time order.</param>
public sealed record TrackView(Track Track, ImmutableArray<ClipView> Clips)
{
    /// <summary>The track's id.</summary>
    public string Id => Track.Id;
}

/// <summary>
/// What one sequence looks like to the timeline, built from a project snapshot.
/// </summary>
/// <remarks>
/// Built again after every command, which is cheap because the model is immutable and a clip
/// that did not change keeps its <see cref="ClipView"/>. Tracks are in display order: subtitles
/// on top, then picture tracks with the highest layer uppermost (the one composited last is the
/// one you see), then sound tracks in order downwards.
/// </remarks>
public sealed class TimelineContent
{
    private readonly Dictionary<string, ClipView> _byId;
    private readonly ILookup<string, ClipView> _byLink;
    private readonly ILookup<string, ClipView> _byGroup;

    private TimelineContent(
        Sequence sequence,
        ProjectSettings settings,
        ImmutableArray<TrackView> tracks,
        Dictionary<string, ClipView> byId)
    {
        Sequence = sequence;
        Settings = settings;
        Tracks = tracks;
        _byId = byId;
        _byLink = byId.Values.Where(clip => clip.Clip.LinkGroupId is not null).ToLookup(clip => clip.Clip.LinkGroupId!, StringComparer.Ordinal);
        _byGroup = byId.Values.Where(clip => clip.Clip.GroupId is not null).ToLookup(clip => clip.Clip.GroupId!, StringComparer.Ordinal);
        Rows = Stack(tracks);
    }

    /// <summary>Nothing to show.</summary>
    public static TimelineContent Empty { get; } = new(
        new Sequence(string.Empty, string.Empty),
        ProjectSettings.Default,
        [],
        new Dictionary<string, ClipView>(StringComparer.Ordinal));

    /// <summary>The sequence shown.</summary>
    public Sequence Sequence { get; }

    /// <summary>Its settings: frame rate and size.</summary>
    public ProjectSettings Settings { get; }

    /// <summary>Every track, in display order.</summary>
    public ImmutableArray<TrackView> Tracks { get; }

    /// <summary>The tracks as bands, in display order.</summary>
    public ImmutableArray<TrackRow> Rows { get; }

    /// <summary>Every clip on every track.</summary>
    public IEnumerable<ClipView> Clips => Tracks.SelectMany(track => track.Clips);

    /// <summary>How many clips there are.</summary>
    public int ClipCount => _byId.Count;

    /// <summary>A clip by id, or null.</summary>
    public ClipView? Clip(string id) => _byId.GetValueOrDefault(id);

    /// <summary>A track by id, or null.</summary>
    public TrackView? Track(string id) => Tracks.FirstOrDefault(track => string.Equals(track.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The clips that go with one when it is clicked: its link group and its selection group,
    /// followed transitively, so a camera clip brings its sound and the sound brings its camera.
    /// </summary>
    public IReadOnlyList<ClipView> Companions(string clipId)
    {
        if (Clip(clipId) is not { } start)
        {
            return [];
        }

        var found = new Dictionary<string, ClipView>(StringComparer.Ordinal) { [start.Id] = start };
        var pending = new Queue<ClipView>([start]);

        while (pending.Count > 0)
        {
            Clip clip = pending.Dequeue().Clip;
            IEnumerable<ClipView> near =
                (clip.LinkGroupId is { } link ? _byLink[link] : [])
                .Concat(clip.GroupId is { } group ? _byGroup[group] : []);

            foreach (ClipView other in near)
            {
                if (found.TryAdd(other.Id, other))
                {
                    pending.Enqueue(other);
                }
            }
        }

        return [.. found.Values];
    }

    /// <summary>
    /// Builds the content for a sequence, reusing every clip view from <paramref name="previous"/>
    /// whose clip has not changed.
    /// </summary>
    public static TimelineContent Build(Project project, string sequenceId, TimelineContent? previous = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.Sequence(sequenceId) is not { } sequence)
        {
            return Empty;
        }

        var byId = new Dictionary<string, ClipView>(StringComparer.Ordinal);
        var tracks = ImmutableArray.CreateBuilder<TrackView>(sequence.Tracks.Length);

        foreach (Track track in DisplayOrder(sequence.Tracks))
        {
            var clips = ImmutableArray.CreateBuilder<ClipView>(track.Clips.Length);

            foreach (Clip clip in track.Clips)
            {
                bool missing = clip.MediaId is { } mediaId && project.MediaItem(mediaId) is null;
                ClipView? kept = previous?.Clip(clip.Id);

                ClipView view = kept is not null
                    && kept.Clip.Equals(clip)
                    && string.Equals(kept.TrackId, track.Id, StringComparison.Ordinal)
                    && kept.Kind == track.Kind
                    && kept.MediaMissing == missing
                        ? kept
                        : new ClipView(clip, track.Id, track.Kind, missing);

                clips.Add(view);
                byId[clip.Id] = view;
            }

            tracks.Add(new TrackView(track, clips.MoveToImmutable()));
        }

        return new TimelineContent(sequence, project.SettingsFor(sequence), tracks.MoveToImmutable(), byId);
    }

    /// <summary>Subtitles on top, picture with the highest layer uppermost, then sound downwards.</summary>
    internal static IEnumerable<Track> DisplayOrder(IEnumerable<Track> tracks)
    {
        Track[] all = [.. tracks];

        return all.Where(track => track.Kind == TrackKind.Subtitle).OrderByDescending(track => track.Order)
            .Concat(all.Where(track => track.Kind is TrackKind.Video or TrackKind.Adjustment).OrderByDescending(track => track.Order))
            .Concat(all.Where(track => track.Kind == TrackKind.Audio).OrderBy(track => track.Order));
    }

    private static ImmutableArray<TrackRow> Stack(ImmutableArray<TrackView> tracks)
    {
        var rows = ImmutableArray.CreateBuilder<TrackRow>(tracks.Length);
        double top = 0.0;

        foreach (TrackView track in tracks)
        {
            double height = Math.Clamp(track.Track.Height, TimelineRowLimits.MinHeight, TimelineRowLimits.MaxHeight);
            rows.Add(new TrackRow(track.Id, track.Track.Kind, top, height));
            top += height;
        }

        return rows.MoveToImmutable();
    }
}

/// <summary>How short and how tall a track may be drawn.</summary>
public static class TimelineRowLimits
{
    /// <summary>Enough for a name and the toggles.</summary>
    public const double MinHeight = 24.0;

    /// <summary>Tall enough for a waveform worth reading.</summary>
    public const double MaxHeight = 400.0;
}
