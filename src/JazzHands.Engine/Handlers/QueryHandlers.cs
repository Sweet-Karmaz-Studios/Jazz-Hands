using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Summarises the project.</summary>
public sealed class GetProjectHandler : IQueryHandler<GetProjectQuery, ProjectInfo>
{
    /// <inheritdoc />
    public ProjectInfo Handle(Project project, GetProjectQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        Sequence? active = project.ActiveSequence;

        return new ProjectInfo(
            project.Id,
            project.Name,
            project.SchemaVersion,
            project.Settings.FrameRate,
            project.Settings.Width,
            project.Settings.Height,
            project.Settings.SampleRate,
            project.Settings.ChannelCount,
            project.Settings.ColorSpace,
            project.Media.Length,
            project.Sequences.Length,
            project.ActiveSequenceId,
            active?.Duration ?? Flicks.Zero,
            context.Session?.ProjectPath ?? string.Empty,
            context.Session?.IsDirty ?? false);
    }
}

/// <summary>Lists the sequences in the project.</summary>
public sealed class ListSequencesHandler : IQueryHandler<ListSequencesQuery, SequenceInfo[]>
{
    /// <inheritdoc />
    public SequenceInfo[] Handle(Project project, ListSequencesQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);

        return [.. project.Sequences.Select(sequence => Describe(project, sequence))];
    }

    internal static SequenceInfo Describe(Project project, Sequence sequence)
    {
        ProjectSettings settings = project.SettingsFor(sequence);

        return new SequenceInfo(
            sequence.Id,
            sequence.Name,
            sequence.Tracks.Length,
            sequence.Tracks.Sum(track => track.Clips.Length),
            sequence.Duration,
            string.Equals(project.ActiveSequenceId, sequence.Id, StringComparison.Ordinal),
            settings.FrameRate,
            settings.Width,
            settings.Height,
            sequence.Settings is not null,
            sequence.IsMagnetic);
    }
}

/// <summary>Lists the tracks of a sequence, bottom of the stack first.</summary>
public sealed class ListTracksHandler : IQueryHandler<ListTracksQuery, TrackInfo[]>
{
    /// <inheritdoc />
    public TrackInfo[] Handle(Project project, ListTracksQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);

        Sequence sequence = HandlerHelp.Sequence(project, query.SequenceId);

        return [.. sequence.Tracks.OrderBy(track => track.Order).Select(track => Describe(sequence, track))];
    }

    internal static TrackInfo Describe(Sequence sequence, Track track) => new(
        track.Id,
        sequence.Id,
        track.Kind,
        track.Name,
        track.Order,
        track.Clips.Length,
        track.Duration,
        track.Locked,
        track.Muted,
        track.Solo,
        TimelineQueries.IsAudible(sequence, track),
        track.Height,
        track.Color,
        track.IsSyncLocked);
}

/// <summary>Lists clips, in timeline order.</summary>
public sealed class ListClipsHandler : IQueryHandler<ListClipsQuery, ClipInfo[]>
{
    /// <inheritdoc />
    public ClipInfo[] Handle(Project project, ListClipsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);

        Sequence sequence = HandlerHelp.Sequence(project, query.SequenceId);

        IEnumerable<Track> tracks = query.TrackId is { Length: > 0 } trackId
            ? [sequence.Track(trackId)
                ?? throw new CommandException("track-not-found", $"No track with id '{trackId}'.")]
            : sequence.Tracks.OrderBy(track => track.Order);

        return
        [
            .. tracks
                .SelectMany(track => track.Clips.Select(clip => Describe(sequence, track, clip)))
                .OrderBy(clip => clip.Start)
                .ThenBy(clip => clip.TrackId, StringComparer.Ordinal),
        ];
    }

    internal static ClipInfo Describe(Sequence sequence, Track track, Clip clip) => new(
        clip.Id,
        track.Id,
        sequence.Id,
        clip.Name,
        clip.Start,
        clip.Duration,
        clip.End,
        clip.SourceIn,
        clip.SourceOut,
        clip.MediaId,
        clip.GeneratorId,
        clip.SequenceId,
        clip.EffectiveSpeed,
        clip.Reverse,
        clip.Enabled,
        clip.BlendMode,
        clip.LinkGroupId,
        clip.GroupId,
        clip.Effects.Length,
        clip.Markers.Length,
        clip.IsHold);
}

/// <summary>Describes one clip.</summary>
public sealed class GetClipHandler : IQueryHandler<GetClipQuery, ClipInfo>
{
    /// <inheritdoc />
    public ClipInfo Handle(Project project, GetClipQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);

        ClipLocation found = HandlerHelp.Clip(project, query.ClipId);
        return ListClipsHandler.Describe(found.Sequence, found.Track, found.Clip);
    }
}

/// <summary>Lists markers, in time order.</summary>
public sealed class ListMarkersHandler : IQueryHandler<ListMarkersQuery, MarkerInfo[]>
{
    /// <inheritdoc />
    public MarkerInfo[] Handle(Project project, ListMarkersQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        var found = ImmutableArray.CreateBuilder<MarkerInfo>();

        if (query.ClipId is { Length: > 0 } clipId)
        {
            ClipLocation location = HandlerHelp.Clip(project, clipId);
            foreach (Marker marker in location.Clip.Markers)
            {
                found.Add(Describe(marker, MarkerOwner.Clip, clipId, location.Clip.Start + marker.Time));
            }
        }
        else
        {
            Sequence sequence = HandlerHelp.Sequence(project, query.SequenceId);

            foreach (Marker marker in sequence.Markers)
            {
                found.Add(Describe(marker, MarkerOwner.Sequence, sequence.Id, marker.Time));
            }

            foreach (Track track in sequence.Tracks)
            {
                foreach (Clip clip in track.Clips)
                {
                    foreach (Marker marker in clip.Markers)
                    {
                        found.Add(Describe(marker, MarkerOwner.Clip, clip.Id, clip.Start + marker.Time));
                    }
                }
            }
        }

        IEnumerable<MarkerInfo> result = found;

        if (query.ChaptersOnly)
        {
            result = result.Where(marker => marker.IsChapter);
        }

        return [.. result.OrderBy(marker => marker.TimelineTime)];
    }

    private static MarkerInfo Describe(Marker marker, MarkerOwner owner, string ownerId, Flicks timelineTime) => new(
        marker.Id,
        owner,
        ownerId,
        marker.Time,
        timelineTime,
        marker.Duration,
        marker.Name,
        marker.Color,
        marker.Note,
        marker.IsChapter);
}

/// <summary>Lists what has been done.</summary>
public sealed class ListHistoryHandler : IQueryHandler<ListHistoryQuery, HistoryInfo[]>
{
    /// <inheritdoc />
    public HistoryInfo[] Handle(Project project, ListHistoryQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Session is null)
        {
            throw new CommandException(
                "no-session",
                "History belongs to a session, and this query was run without one.");
        }

        return [.. context.Session.History(Math.Max(0, query.Limit))];
    }
}
