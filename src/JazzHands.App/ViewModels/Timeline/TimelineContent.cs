using System.Collections.Immutable;
using JazzHands.App.Controls.Timeline;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
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
/// <param name="Cuts">For a multicam clip (Phase 41), where on the timeline its picture cuts to another angle.</param>
public sealed record ClipView(Clip Clip, string TrackId, TrackKind Kind, bool MediaMissing, EquatableArray<Flicks> Cuts = default)
{
    /// <summary>The clip's id.</summary>
    public string Id => Clip.Id;

    /// <summary>Where it starts.</summary>
    public Flicks Start => Clip.Start;

    /// <summary>Where it ends, exclusive.</summary>
    public Flicks End => Clip.End;

    /// <summary>
    /// What the body shows: the clip's name, and its speed when it is not 1x. A caption shows its
    /// text, both lines: its name is only the first (a two line caption showed half, 2026-09-30).
    /// </summary>
    public string Label { get; } = Clip.Cue is { } cue
        ? string.Join('\n', Core.Titles.TitleMarkup.PlainText(cue.Text).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).Take(2))
        : Clip.EffectiveSpeed == Rational.One
            ? Clip.Name
            : $"{Clip.Name}  {Clip.EffectiveSpeed.ToDouble() * 100:0.#}%{(Clip.Reverse ? " reversed" : string.Empty)}";
}

/// <summary>One transition as the timeline draws it: where it plays, not only where its cut is.</summary>
/// <param name="Transition">The transition.</param>
/// <param name="TrackId">The track it is on.</param>
/// <param name="Range">Where it plays, fitted to its clips.</param>
/// <param name="Cut">The cut it sits on.</param>
/// <param name="Name">What the bar says: the type's name.</param>
/// <param name="Holds">True when a clip is short of source for it, so it holds a frame: drawn with a warning.</param>
public sealed record TransitionView(Transition Transition, string TrackId, TimeRange Range, Flicks Cut, string Name, bool Holds)
{
    /// <summary>The transition's id.</summary>
    public string Id => Transition.Id;

    /// <summary>Where it starts.</summary>
    public Flicks Start => Range.Start;

    /// <summary>Where it ends, exclusive.</summary>
    public Flicks End => Range.End;
}

/// <summary>One track, its clips and its transitions, as the timeline draws them.</summary>
/// <param name="Track">The track.</param>
/// <param name="Clips">Its clips, in time order.</param>
public sealed record TrackView(Track Track, ImmutableArray<ClipView> Clips)
{
    /// <summary>Its transitions that play, in time order.</summary>
    public ImmutableArray<TransitionView> Transitions { get; init; } = [];

    /// <summary>The track's id.</summary>
    public string Id => Track.Id;

    /// <summary>Where its clips sit in its row: overlapping cues side by side, one lane on any other track.</summary>
    public Controls.Timeline.CueLanes Lanes { get; } = Controls.Timeline.CueLanes.Of(Track.Kind, Clips);
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

    /// <summary>Where a multicam clip's picture cuts to another angle, on the timeline, inside the clip; empty for any other clip.</summary>
    internal static EquatableArray<Flicks> MulticamCuts(Project project, Clip clip)
    {
        if (clip.SequenceId is not { } id || project.Sequence(id)?.Multicam is not { } multicam || clip.Reverse || clip.IsRemapped || clip.EffectiveSpeed != Rational.One)
        {
            return default;
        }

        return [.. multicam.Changes().Skip(1)
            .Select(change => change.At - clip.SourceIn + clip.Start)
            .Where(at => at > clip.Start && at < clip.End)];
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
        Rational frameRate = project.SettingsFor(sequence).FrameRate;

        foreach (Track track in DisplayOrder(sequence.Tracks))
        {
            var clips = ImmutableArray.CreateBuilder<ClipView>(track.Clips.Length);

            foreach (Clip clip in track.Clips)
            {
                bool missing = clip.MediaId is { } mediaId && project.MediaItem(mediaId) is null;
                EquatableArray<Flicks> cuts = MulticamCuts(project, clip);
                ClipView? kept = previous?.Clip(clip.Id);

                ClipView view = kept is not null
                    && kept.Clip.Equals(clip)
                    && string.Equals(kept.TrackId, track.Id, StringComparison.Ordinal)
                    && kept.Kind == track.Kind
                    && kept.MediaMissing == missing
                    && kept.Cuts == cuts
                        ? kept
                        : new ClipView(clip, track.Id, track.Kind, missing, cuts);

                clips.Add(view);
                byId[clip.Id] = view;
            }

            // A track nothing changed on keeps its view too, so the timeline keeps its drawing.
            ImmutableArray<ClipView> views = clips.MoveToImmutable();
            ImmutableArray<TransitionView> transitions = TransitionViews(project, track, frameRate);
            TrackView? before = previous?.Track(track.Id);
            tracks.Add(before is not null && ReferenceEquals(before.Track, track) && Same(before.Clips, views) && before.Transitions.SequenceEqual(transitions)
                ? before
                : new TrackView(track, views) { Transitions = transitions });
        }

        return new TimelineContent(sequence, project.SettingsFor(sequence), tracks.MoveToImmutable(), byId);
    }

    /// <summary>True when two lists hold the very same clip views.</summary>
    private static bool Same(ImmutableArray<ClipView> before, ImmutableArray<ClipView> now)
    {
        if (before.Length != now.Length)
        {
            return false;
        }

        for (int index = 0; index < now.Length; index++)
        {
            if (!ReferenceEquals(before[index], now[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A transition by id, or null.</summary>
    public TransitionView? Transition(string id) =>
        Tracks.SelectMany(track => track.Transitions).FirstOrDefault(view => string.Equals(view.Id, id, StringComparison.Ordinal));

    /// <summary>Every transition on every track.</summary>
    public IEnumerable<TransitionView> Transitions => Tracks.SelectMany(track => track.Transitions);

    /// <summary>The transitions of a track that play, with where, and whether a clip is short of source for one.</summary>
    private static ImmutableArray<TransitionView> TransitionViews(Project project, Track track, Rational frameRate)
    {
        if (track.Transitions.IsEmpty)
        {
            return [];
        }

        var views = ImmutableArray.CreateBuilder<TransitionView>();
        foreach (TransitionSpan span in TransitionTiming.Spans(track, frameRate))
        {
            (Flicks leftShort, Flicks rightShort) = TransitionTiming.Shortfall(project, span);
            string name = Engine.Effects.EffectCatalog.Registry.Find(span.Transition.TypeId)?.Name ?? span.Transition.TypeId;
            views.Add(new TransitionView(span.Transition, track.Id, span.Range, span.Cut, name, leftShort.Value > 0 || rightShort.Value > 0));
        }

        return views.ToImmutable();
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
