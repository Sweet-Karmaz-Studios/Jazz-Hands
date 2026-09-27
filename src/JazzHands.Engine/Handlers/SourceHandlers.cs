using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>What the source monitor handlers share.</summary>
internal static class SourceHelp
{
    /// <summary>The session's source monitor, or a fresh one for a lone command.</summary>
    internal static SourceMonitor Monitor(IServiceProvider? services) =>
        services?.GetService<SourceMonitor>() ?? new SourceMonitor();

    /// <summary>The media item the monitor has open, refusing when there is none or it has gone.</summary>
    internal static MediaItem Open(Project project, SourceMonitor monitor)
    {
        if (monitor.MediaId is not { } id)
        {
            throw new CommandException("no-source", "The source monitor has nothing open. Open a media item with source.open first.");
        }

        return project.MediaItem(id)
            ?? throw new CommandException("no-source", "The item in the source monitor is no longer in the project. Open another.");
    }

    /// <summary>The frame rate a file's marks snap to: its picture's, or the project's for sound.</summary>
    internal static Rational Rate(Project project, MediaItem item) =>
        item.Info?.VideoStreams.FirstOrDefault()?.FrameRate is { IsZero: false } rate ? rate : project.Settings.FrameRate;

    /// <summary>A source time, checked against the file and put on its frame.</summary>
    internal static Flicks Frame(Project project, MediaItem item, Flicks at)
    {
        if (at < Flicks.Zero || at > item.Duration)
        {
            throw new CommandException("time-out-of-range", $"'{item.Name}' runs {Timecode.FormatClock(item.Duration)}; that is outside it.");
        }

        return at.SnapToFrame(Rate(project, item));
    }

    /// <summary>
    /// Works out a three-point edit from a command's options, the source monitor and the
    /// sequence, and where each stream goes.
    /// </summary>
    internal static (Sequence Sequence, MediaItem Media, ThreePoint Edit, Track? Picture, MediaStream? PictureStream, Track?[] Sound, MediaStream[] SoundStreams) Plan(
        Project project,
        string? mediaId,
        Flicks? sourceIn,
        Flicks? sourceOut,
        Flicks? at,
        string? sequenceId,
        IServiceProvider? services)
    {
        Sequence sequence = HandlerHelp.Sequence(project, sequenceId);
        SourceMonitor monitor = Monitor(services);
        MediaItem media = mediaId is { Length: > 0 } given ? MediaServices.Require(project, given) : Open(project, monitor);

        // The monitor's marks count only for the item it has open.
        bool fromMonitor = string.Equals(monitor.MediaId, media.Id, StringComparison.Ordinal);
        Flicks? markIn = sourceIn ?? (fromMonitor ? monitor.In : null);
        Flicks? markOut = sourceOut ?? (fromMonitor ? monitor.Out : null);

        Flicks playhead = at
            ?? PlaybackHelp.TryController(services)?.Position
            ?? (sequence.InOut is not null
                ? Flicks.Zero
                : throw new CommandException("no-playhead", "There is no playhead in a headless session. Say where with --at, or set the sequence's in point."));

        ThreePoint edit = HandlerContext.Require(ThreePointOps.Resolve(markIn, markOut, media, sequence.InOut, sequence.Duration, playhead));

        MediaStream? pictureStream = media.Info?.VideoStreams.FirstOrDefault();
        MediaStream[] soundStreams = [.. media.Info?.AudioStreams ?? []];
        (Track? picture, Track?[] sound) = ThreePointOps.Targets(sequence, soundStreams.Length);
        if (pictureStream is null && media.Kind == MediaKind.Movie)
        {
            picture = null;
        }

        if (picture is null && sound.All(track => track is null) && sequence.SourcePatch is not null)
        {
            throw new CommandException("no-target", "No track is targeted for this source. Target one with track.set-target, or the switches at the head of the tracks.");
        }

        return (sequence, media, edit, picture, pictureStream, sound, soundStreams);
    }
}

/// <summary>Opens an item in the source monitor.</summary>
public sealed class OpenSourceHandler : ICommandHandler<OpenSourceCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, OpenSourceCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, command.MediaId);
        Flicks at = SourceHelp.Frame(project, item, command.At ?? item.DefaultIn);
        SourceHelp.Monitor(context.Services).Open(item, at);
        return project;
    }
}

/// <summary>Moves the source playhead.</summary>
public sealed class SeekSourceHandler : ICommandHandler<SeekSourceCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SeekSourceCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        SourceMonitor monitor = SourceHelp.Monitor(context.Services);
        MediaItem item = SourceHelp.Open(project, monitor);
        monitor.Seek(SourceHelp.Frame(project, item, command.At));
        return project;
    }
}

/// <summary>Marks the source in.</summary>
public sealed class SetSourceInHandler : ICommandHandler<SetSourceInCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetSourceInCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        SourceMonitor monitor = SourceHelp.Monitor(context.Services);
        MediaItem item = SourceHelp.Open(project, monitor);
        Flicks mark = SourceHelp.Frame(project, item, command.At ?? monitor.Position);

        // An out at or before the new in goes, as in every editor.
        monitor.Mark(mark, monitor.Out is { } end && end > mark ? end : null);
        return project;
    }
}

/// <summary>Marks the source out.</summary>
public sealed class SetSourceOutHandler : ICommandHandler<SetSourceOutCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetSourceOutCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        SourceMonitor monitor = SourceHelp.Monitor(context.Services);
        MediaItem item = SourceHelp.Open(project, monitor);
        Flicks frame = Flicks.FromFrames(1, SourceHelp.Rate(project, item));

        // The frame at the out mark is taken, so the range ends one frame later, within the file.
        Flicks end = Flicks.Min(SourceHelp.Frame(project, item, command.At ?? monitor.Position) + frame, item.Duration);
        monitor.Mark(monitor.In is { } start && start < end ? start : null, end);
        return project;
    }
}

/// <summary>Clears the source marks.</summary>
public sealed class ClearSourceInOutHandler : ICommandHandler<ClearSourceInOutCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ClearSourceInOutCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        SourceHelp.Monitor(context.Services).Mark(null, null);
        return project;
    }
}

/// <summary>Plays or pauses the source monitor.</summary>
public sealed class PlaySourceHandler : ICommandHandler<PlaySourceCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, PlaySourceCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        SourceMonitor monitor = SourceHelp.Monitor(context.Services);
        _ = SourceHelp.Open(project, monitor);
        if (monitor.Player is null)
        {
            throw new CommandException("no-playback", "Playing the source needs a running editor. The marks and edits work headless.");
        }

        monitor.SetPlaying(command.Play ?? !monitor.IsPlaying);
        return project;
    }
}

/// <summary>Says what the source monitor has open.</summary>
public sealed class GetSourceStateHandler : IQueryHandler<GetSourceStateQuery, SourceStateInfo>
{
    /// <inheritdoc />
    public SourceStateInfo Handle(Project project, GetSourceStateQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        SourceMonitor monitor = SourceHelp.Monitor(context.Services);
        MediaItem? item = monitor.MediaId is { } id ? project.MediaItem(id) : null;
        Rational rate = item is null ? project.Settings.FrameRate : SourceHelp.Rate(project, item);
        return new SourceStateInfo(
            item?.Id,
            item?.Name,
            monitor.Position,
            monitor.In,
            monitor.Out,
            item?.Duration ?? Flicks.Zero,
            item?.Info?.VideoStreams.FirstOrDefault()?.FrameRate,
            Timecode.Format(monitor.Position, rate),
            monitor.IsPlaying);
    }
}

/// <summary>Says what an edit from the source would do.</summary>
public sealed class PlanFromSourceHandler : IQueryHandler<PlanFromSourceQuery, SourceEditPlan>
{
    /// <inheritdoc />
    public SourceEditPlan Handle(Project project, PlanFromSourceQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        (_, MediaItem media, ThreePoint edit, Track? picture, _, Track?[] sound, _) =
            SourceHelp.Plan(project, query.MediaId, query.SourceIn, query.SourceOut, query.At, query.SequenceId, context.Services);
        return new SourceEditPlan(media.Id, edit.SourceIn, edit.Duration, edit.At, picture?.Id, [.. sound.Select(track => track?.Id ?? string.Empty)], edit.Note);
    }
}

/// <summary>Inserts and overwrites from the source: the two differ only in how they make room.</summary>
internal static class SourceEdit
{
    internal static Project Apply(
        Project project,
        string? mediaId,
        Flicks? sourceIn,
        Flicks? sourceOut,
        Flicks? at,
        string? sequenceId,
        bool insert,
        HandlerContext context)
    {
        (Sequence sequence, MediaItem media, ThreePoint edit, Track? picture, MediaStream? pictureStream, Track?[] sound, MediaStream[] soundStreams) =
            SourceHelp.Plan(project, mediaId, sourceIn, sourceOut, at, sequenceId, context.Services);
        SequenceEdit.RefuseQuickTrim(sequence);

        // With no patch, a sound stream past the last audio track gets a track of its own, as a
        // drop onto the timeline does; with one, it is left out.
        if (sequence.SourcePatch is null)
        {
            for (int stream = 0; stream < sound.Length; stream++)
            {
                if (sound[stream] is null)
                {
                    string name = soundStreams[stream].Title is { Length: > 0 } title ? title : $"A{stream + 1}";
                    var made = new Track(Id.New(), TrackKind.Audio, name, sequence.NextTrackOrder());
                    sequence = sequence.AddTrack(made);
                    sound[stream] = made;
                    context.Changed(made.Id);
                }
            }

            project = project.ReplaceSequence(sequence);
        }

        string[] targets = [.. new[] { picture }.Concat(sound).OfType<Track>().Select(track => track.Id).Distinct()];
        Sequence before = sequence;
        Sequence room = insert
            ? HandlerContext.Require(EditOps.Ripple(sequence, edit.At, edit.Duration, targets))
            : HandlerContext.Require(EditOps.LiftRange(sequence, new TimeRange(edit.At, edit.Duration), targets));
        project = SequenceEdit.Commit(project, before, room, context);

        var range = new TimeRange(edit.At, edit.Duration);
        int placed = (picture is not null && pictureStream is not null ? 1 : 0) + sound.Count(track => track is not null);
        string? link = placed > 1 ? Id.New() : null;
        var clips = new List<(Track Track, Clip Clip)>();
        if (picture is not null && pictureStream is not null)
        {
            clips.Add((picture, new Clip(Id.New(), range, edit.SourceIn, MediaId: media.Id, SourceStreamIndex: pictureStream.Index, LinkGroupId: link, Name: media.Name)));
        }
        else if (picture is not null && media.IsImages)
        {
            clips.Add((picture, new Clip(Id.New(), range, edit.SourceIn, MediaId: media.Id, LinkGroupId: link, Name: media.Name)));
        }

        for (int stream = 0; stream < sound.Length; stream++)
        {
            if (sound[stream] is { } track)
            {
                clips.Add((track, new Clip(Id.New(), range, edit.SourceIn, MediaId: media.Id, SourceStreamIndex: soundStreams[stream].Index, LinkGroupId: link, Name: media.Name)));
            }
        }

        foreach ((Track target, Clip clip) in clips)
        {
            Track now = project.Sequence(sequence.Id)!.Track(target.Id)!;
            if (EditOps.Overlaps(now, clip))
            {
                throw new CommandException("would-overlap", $"Something on '{now.Name}' is in the way and could not be moved: a locked or sync-locked clip.");
            }

            project = project.ReplaceTrack(now.AddClip(clip));
            context.Changed(clip.Id);
            context.Changed(now.Id);
        }

        return project;
    }
}

/// <summary>Inserts the source's marked stretch.</summary>
public sealed class InsertFromSourceHandler : ICommandHandler<InsertFromSourceCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, InsertFromSourceCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return SourceEdit.Apply(project, command.MediaId, command.SourceIn, command.SourceOut, command.At, command.SequenceId, insert: true, context);
    }
}

/// <summary>Overwrites with the source's marked stretch.</summary>
public sealed class OverwriteFromSourceHandler : ICommandHandler<OverwriteFromSourceCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, OverwriteFromSourceCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        return SourceEdit.Apply(project, command.MediaId, command.SourceIn, command.SourceOut, command.At, command.SequenceId, insert: false, context);
    }
}

/// <summary>Targets a track, or not.</summary>
public sealed class SetTrackTargetHandler : ICommandHandler<SetTrackTargetCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackTargetCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);
        if (track.Kind is not (TrackKind.Video or TrackKind.Audio))
        {
            throw new CommandException("not-a-target", $"'{track.Name}' is a {track.Kind.ToString().ToLowerInvariant()} track; only picture and sound tracks take edits from the source.");
        }

        if (command.On)
        {
            HandlerHelp.RequireUnlocked(track);
        }

        if (ThreePointOps.TargetedTracks(sequence).Contains(track) == command.On)
        {
            return project;
        }

        Sequence patched = ThreePointOps.SetTarget(sequence, track, command.On);

        context.Changed(sequence.Id);
        context.Changed(track.Id);
        return project.ReplaceSequence(patched);
    }
}

/// <summary>Finds where a source frame is shown in a sequence.</summary>
public sealed class FindSourceFrameHandler : IQueryHandler<FindSourceFrameQuery, SourceFrameInfo>
{
    /// <inheritdoc />
    public SourceFrameInfo Handle(Project project, FindSourceFrameQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        MediaItem item = MediaServices.Require(project, query.MediaId);
        Sequence sequence = HandlerHelp.Sequence(project, query.SequenceId);
        Rational rate = project.SettingsFor(sequence).FrameRate;
        Flicks frame = Flicks.FromFrames(1, rate);

        // Picture before sound, lowest track first, earliest first: the first frame of the
        // sequence that shows that source frame. A timeline frame shows it when the source time
        // there is on it, to within a frame.
        foreach (Track track in sequence.Tracks.OrderBy(track => track.Kind == TrackKind.Audio).ThenBy(track => track.Order))
        {
            foreach (Clip clip in track.Clips)
            {
                if (!string.Equals(clip.MediaId, item.Id, StringComparison.Ordinal) || clip.IsHold)
                {
                    continue;
                }

                Flicks low = Flicks.Min(clip.SourceIn, clip.SourceOut);
                Flicks high = Flicks.Max(clip.SourceIn, clip.SourceOut);
                if (query.At < low || query.At >= high)
                {
                    continue;
                }

                for (Flicks at = clip.Start.SnapToFrame(rate, RoundingMode.Ceiling); at < clip.End; at += frame)
                {
                    Flicks shown = clip.SourceTimeAt(at);
                    Flicks next = clip.SourceTimeAt(at + frame);
                    if (Flicks.Min(shown, next) <= query.At && query.At < Flicks.Max(shown, next))
                    {
                        return new SourceFrameInfo(clip.Id, track.Id, at);
                    }
                }
            }
        }

        throw new CommandException("not-in-sequence", $"That frame of '{item.Name}' is not used in '{sequence.Name}'.");
    }
}
