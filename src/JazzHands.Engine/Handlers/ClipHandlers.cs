using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Puts a clip on a track.</summary>
public sealed class AddClipHandler : ICommandHandler<AddClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);
        HandlerHelp.RequireUnlocked(track);

        int sources = (command.MediaId is null ? 0 : 1)
            + (command.GeneratorId is null ? 0 : 1)
            + (command.SequenceId is null ? 0 : 1);

        if (sources != 1)
        {
            throw new CommandException(
                sources == 0 ? "clip-without-source" : "clip-with-many-sources",
                "A clip plays exactly one of --media, --generator or --sequence.");
        }

        if (command.MediaId is { } mediaId && project.MediaItem(mediaId) is null)
        {
            throw new CommandException("missing-media-reference", $"No media with id '{mediaId}' in this project.");
        }

        if (command.SequenceId is { } nestedId)
        {
            if (project.Sequence(nestedId) is null)
            {
                throw new CommandException("missing-sequence-reference", $"No sequence with id '{nestedId}'.");
            }

            if (string.Equals(nestedId, sequence.Id, StringComparison.Ordinal))
            {
                throw new CommandException("sequence-cycle", "A sequence cannot nest itself.");
            }
        }

        Flicks duration = DurationOf(project, command);
        if (duration <= Flicks.Zero)
        {
            throw new CommandException("empty-clip", "A clip needs a duration greater than zero.");
        }

        if (command.At < Flicks.Zero)
        {
            throw new CommandException("time-out-of-range", "A clip cannot start before the timeline does.");
        }

        string id = HandlerHelp.IdOr(command.ClipId);
        HandlerHelp.RequireUnused(project, id);

        MediaItem? media = command.MediaId is { } playing ? project.MediaItem(playing) : null;
        MediaStream[] linkedAudio = command.WithAudio && track.Kind == TrackKind.Video && media?.Info is { } info
            ? [.. info.AudioStreams]
            : [];

        var clip = new Clip(
            id,
            new TimeRange(command.At, duration),
            command.SourceIn ?? Flicks.Zero,
            MediaId: command.MediaId,
            GeneratorId: command.GeneratorId,
            SequenceId: command.SequenceId,
            SourceStreamIndex: command.SourceStreamIndex ?? DefaultStream(media, track.Kind),
            LinkGroupId: linkedAudio.Length > 0 ? Id.New() : null,
            Name: command.Name ?? DefaultName(project, command));

        if (EditOps.Overlaps(track, clip))
        {
            throw new CommandException(
                "would-overlap",
                $"A clip already occupies that part of '{track.Name}'. Move it, or pick another time.");
        }

        context.Changed(id);
        context.Changed(track.Id);
        Project result = project.ReplaceTrack(track.AddClip(clip));

        for (int index = 0; index < linkedAudio.Length; index++)
        {
            result = AddLinkedAudio(result, sequence.Id, clip, linkedAudio[index], index, context);
        }

        return result;
    }

    /// <summary>
    /// Puts one audio stream of a movie on an audio track, linked to its picture.
    /// </summary>
    /// <remarks>
    /// The track is found by the stream's title, which is how OBS names its tracks, so a second
    /// capture dropped on the timeline lands its microphone on the same Mic track as the first.
    /// A stream with no title takes A1, A2 and so on by its place among the audio streams, which
    /// puts a camera's one stream on the A1 every project starts with. When the track is locked
    /// or already has something there, a new track of that name is made rather than the stream
    /// being dropped or anything being moved.
    /// </remarks>
    private static Project AddLinkedAudio(Project project, string sequenceId, Clip picture, MediaStream stream, int index, HandlerContext context)
    {
        Sequence sequence = project.Sequence(sequenceId)!;
        string name = stream.Title is { Length: > 0 } title ? title : $"A{index + 1}";

        var audio = new Clip(
            Id.New(),
            picture.Range,
            picture.SourceIn,
            MediaId: picture.MediaId,
            SourceStreamIndex: stream.Index,
            LinkGroupId: picture.LinkGroupId,
            Name: picture.Name);

        Track? target = sequence.Tracks.FirstOrDefault(candidate =>
            candidate.Kind == TrackKind.Audio
            && !candidate.Locked
            && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
            && !EditOps.Overlaps(candidate, audio));

        // No track by that name yet: an empty audio track still called by its default name (the
        // A1 every project starts with) takes the stream and the stream's name, so the first
        // capture's game, microphone and Discord land on A1, A2 and A3 rather than past an empty A1.
        if (target is null && stream.Title is { Length: > 0 })
        {
            Track? unused = sequence.Tracks
                .Where(candidate => candidate.Kind == TrackKind.Audio && !candidate.Locked && candidate.Clips.IsEmpty && IsDefaultAudioName(candidate.Name))
                .OrderBy(candidate => candidate.Order)
                .FirstOrDefault();

            if (unused is not null)
            {
                target = unused with { Name = name };
                project = project.ReplaceTrack(target);
            }
        }

        if (target is null)
        {
            target = new Track(Id.New(), TrackKind.Audio, name, sequence.NextTrackOrder());
            project = project.ReplaceSequence(sequence.AddTrack(target));
            context.Changed(sequence.Id);
        }

        context.Changed(audio.Id);
        context.Changed(target.Id);
        return project.ReplaceTrack(target.AddClip(audio));
    }

    /// <summary>How long the clip a command adds runs: what it says, or the rest of its source.</summary>
    internal static Flicks DurationOf(Project project, AddClipCommand command) =>
        command.Duration ?? DefaultDuration(project, command);

    /// <summary>
    /// The existing, unlocked audio tracks a movie's linked sound would be put on by name, which is
    /// where an overwrite has to make room.
    /// </summary>
    internal static IEnumerable<string> LinkedAudioTracks(Project project, Sequence sequence, AddClipCommand command, Track track)
    {
        if (!command.WithAudio || track.Kind != TrackKind.Video || command.MediaId is not { } mediaId || project.MediaItem(mediaId)?.Info is not { } info)
        {
            yield break;
        }

        int index = 0;
        foreach (MediaStream stream in info.AudioStreams)
        {
            string name = stream.Title is { Length: > 0 } title ? title : $"A{index + 1}";
            index++;

            if (sequence.Tracks.FirstOrDefault(candidate =>
                candidate.Kind == TrackKind.Audio && !candidate.Locked && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                yield return found.Id;
            }
        }
    }

    /// <summary>True for A1, A2 and so on: a name nobody chose.</summary>
    private static bool IsDefaultAudioName(string name) =>
        name.Length > 1 && (name[0] == 'A' || name[0] == 'a') && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>The first stream of the kind the track carries, or 0 when the media was never probed.</summary>
    private static int DefaultStream(MediaItem? media, TrackKind kind)
    {
        MediaStreamKind wanted = kind == TrackKind.Audio ? MediaStreamKind.Audio : MediaStreamKind.Video;
        return media?.Info?.Streams.FirstOrDefault(stream => stream.Kind == wanted)?.Index ?? 0;
    }

    private static Flicks DefaultDuration(Project project, AddClipCommand command)
    {
        if (command.MediaId is { } mediaId && project.MediaItem(mediaId) is { } media)
        {
            Flicks remaining = media.Duration - (command.SourceIn ?? Flicks.Zero);
            return remaining > Flicks.Zero ? remaining : Flicks.Zero;
        }

        if (command.SequenceId is { } nestedId && project.Sequence(nestedId) is { } nested)
        {
            return nested.Duration;
        }

        // A generator has no natural length, so it gets the five seconds every editor uses for a
        // title dropped on a timeline.
        return Flicks.FromSeconds(5);
    }

    private static string DefaultName(Project project, AddClipCommand command) =>
        command.MediaId is { } mediaId && project.MediaItem(mediaId) is { } media ? media.Name
        : command.SequenceId is { } nestedId && project.Sequence(nestedId) is { } nested ? nested.Name
        : command.GeneratorId ?? string.Empty;
}

/// <summary>Takes a clip off its track.</summary>
public sealed class RemoveClipHandler : ICommandHandler<RemoveClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        if (command.Ripple)
        {
            // The same edit as clip.ripple-delete: every sync-locked track closes up too.
            SequenceEdit.RefuseQuickTrim(found.Sequence);
            Sequence rippled = HandlerContext.Require(EditOps.RippleDelete(found.Sequence, [command.ClipId]));
            return SequenceEdit.Commit(project, found.Sequence, rippled, context);
        }

        Track updated = HandlerContext.Require(EditOps.Lift(found.Track, command.ClipId));

        context.Changed(command.ClipId);
        context.Changed(found.Track.Id);
        return project.ReplaceTrack(updated);
    }
}

/// <summary>Cuts a clip in two at a timeline time.</summary>
public sealed class SplitClipHandler : ICommandHandler<SplitClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SplitClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        string newId = HandlerHelp.IdOr(command.NewClipId);
        HandlerHelp.RequireUnused(project, newId);

        Track updated = HandlerContext.Require(EditOps.Split(found.Track, command.ClipId, command.At, newId));

        context.Changed(command.ClipId);
        context.Changed(newId);
        context.Changed(found.Track.Id);
        return project.ReplaceTrack(updated);
    }
}

/// <summary>Moves a clip's start or end without moving the other.</summary>
public sealed class TrimClipHandler : ICommandHandler<TrimClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, TrimClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        if (command.In is null && command.Out is null)
        {
            throw new CommandException("nothing-to-do", "A trim needs --in, --out, or both.");
        }

        if (command.Ripple)
        {
            // The same edit as clip.ripple-trim, one edge after the other: every sync-locked
            // track moves too.
            Sequence sequence = found.Sequence;
            SequenceEdit.RefuseQuickTrim(sequence);
            Func<Clip, Flicks?> sourceLength = SequenceEdit.SourceLength(project);

            if (command.In is { } rippleIn)
            {
                sequence = HandlerContext.Require(EditOps.RippleTrim(sequence, [command.ClipId], ClipEdge.Start, rippleIn, sourceLength));
            }

            if (command.Out is { } rippleOut)
            {
                sequence = HandlerContext.Require(EditOps.RippleTrim(sequence, [command.ClipId], ClipEdge.End, rippleOut, sourceLength));
            }

            return SequenceEdit.Commit(project, found.Sequence, sequence, context);
        }

        Track track = found.Track;

        if (command.In is { } inPoint)
        {
            track = HandlerContext.Require(EditOps.TrimIn(track, command.ClipId, inPoint, ripple: false));
        }

        if (command.Out is { } outPoint)
        {
            track = HandlerContext.Require(EditOps.TrimOut(track, command.ClipId, outPoint, ripple: false));
        }

        context.Changed(command.ClipId);
        context.Changed(found.Track.Id);
        return project.ReplaceTrack(track);
    }
}

/// <summary>Moves the cut between two touching clips.</summary>
public sealed class RollClipsHandler : ICommandHandler<RollClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RollClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation left = HandlerHelp.Clip(project, command.LeftClipId);
        HandlerHelp.RequireUnlocked(left.Track);

        if (left.Track.Clip(command.RightClipId) is null)
        {
            throw new CommandException(
                "not-adjacent",
                $"'{command.RightClipId}' is not on the same track as '{command.LeftClipId}'.");
        }

        Track updated = HandlerContext.Require(
            EditOps.Roll(left.Track, command.LeftClipId, command.RightClipId, command.By));

        context.Changed(command.LeftClipId);
        context.Changed(command.RightClipId);
        context.Changed(left.Track.Id);
        return project.ReplaceTrack(updated);
    }
}

/// <summary>Changes which part of the source a clip shows.</summary>
public sealed class SlipClipHandler : ICommandHandler<SlipClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SlipClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        Flicks? sourceDuration = SourceDuration(project, found.Clip);
        Track updated = HandlerContext.Require(
            EditOps.Slip(found.Track, command.ClipId, command.By, sourceDuration));

        context.Changed(command.ClipId);
        context.Changed(found.Track.Id);
        return project.ReplaceTrack(updated);
    }

    internal static Flicks? SourceDuration(Project project, Clip clip) =>
        clip.MediaId is { } mediaId && project.MediaItem(mediaId) is { } media ? media.Duration
        : clip.SequenceId is { } nestedId && project.Sequence(nestedId) is { } nested ? nested.Duration
        : null;
}

/// <summary>Moves a clip, taking the time out of its neighbours.</summary>
public sealed class SlideClipHandler : ICommandHandler<SlideClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SlideClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        Track updated = HandlerContext.Require(EditOps.Slide(found.Track, command.ClipId, command.By));

        context.Changed(command.ClipId);
        context.Changed(found.Track.Id);

        // The neighbours changed length, and the caller has no way to know which they were.
        int index = found.Track.IndexOf(command.ClipId);
        if (index > 0)
        {
            context.Changed(found.Track.Clips[index - 1].Id);
        }

        if (index >= 0 && index < found.Track.Clips.Length - 1)
        {
            context.Changed(found.Track.Clips[index + 1].Id);
        }

        return project.ReplaceTrack(updated);
    }
}

/// <summary>Moves a clip to another time, and optionally to another track.</summary>
public sealed class MoveClipHandler : ICommandHandler<MoveClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, MoveClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        Sequence sequence = found.Sequence;

        if (sequence.IsMagnetic
            && sequence.PrimaryTrack is { Locked: false } primary
            && string.Equals(command.ToTrackId ?? found.Track.Id, primary.Id, StringComparison.Ordinal))
        {
            return OntoStoryline(project, sequence, primary, found, command.To, context);
        }

        if (command.ToTrackId is { } toTrackId)
        {
            Track destination = sequence.Track(toTrackId)
                ?? throw new CommandException(
                    "track-not-found",
                    $"No track with id '{toTrackId}' in '{sequence.Name}'. A clip cannot move to another sequence.");

            HandlerHelp.RequireUnlocked(destination);
            context.Changed(destination.Id);
        }

        Sequence updated = HandlerContext.Require(
            EditOps.Move(sequence, command.ClipId, command.ToTrackId, command.To));

        context.Changed(command.ClipId);
        context.Changed(found.Track.Id);
        return project.ReplaceSequence(updated);
    }

    /// <summary>
    /// A move that ends on a magnetic primary track: along it, the clip comes out and goes back in
    /// at the nearest cut; from another track, it goes in where it was dropped, pushing the rest on.
    /// Either way nothing lands on top of anything and no gap is left.
    /// </summary>
    private static Project OntoStoryline(Project project, Sequence sequence, Track primary, ClipLocation found, Flicks to, HandlerContext context)
    {
        Sequence updated = string.Equals(found.Track.Id, primary.Id, StringComparison.Ordinal)
            ? HandlerContext.Require(EditOps.MoveOnStoryline(sequence, [found.Clip.Id], to))
            : HandlerContext.Require(EditOps.Insert(
                sequence.ReplaceTrack(HandlerContext.Require(EditOps.Lift(found.Track, found.Clip.Id))),
                to,
                [new Placement(primary.Id, found.Clip)]));

        return SequenceEdit.Commit(project, sequence, updated, context);
    }
}

/// <summary>Puts a copy of a clip somewhere else.</summary>
public sealed class CopyClipHandler : ICommandHandler<CopyClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, CopyClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);

        Track destination = command.ToTrackId is { } toTrackId
            ? found.Sequence.Track(toTrackId)
                ?? throw new CommandException("track-not-found", $"No track with id '{toTrackId}'.")
            : found.Track;

        HandlerHelp.RequireUnlocked(destination);

        string newId = HandlerHelp.IdOr(command.NewClipId);
        HandlerHelp.RequireUnused(project, newId);

        Clip copy = Copy(found.Clip, newId, command.At);

        if (EditOps.Overlaps(destination, copy))
        {
            throw new CommandException(
                "would-overlap",
                $"A clip already occupies that part of '{destination.Name}'.");
        }

        context.Changed(newId);
        context.Changed(destination.Id);
        return project.ReplaceTrack(destination.AddClip(copy));
    }

    /// <summary>
    /// A copy at a new time, with fresh identifiers for everything inside it.
    /// </summary>
    /// <remarks>
    /// The effects and markers on a clip carry their own ids, and two of anything sharing an id
    /// would make "which one did you mean" unanswerable. The link group is deliberately dropped:
    /// a copy is not in sync with the original, and inheriting the link would drag the original
    /// about whenever the copy moved.
    /// </remarks>
    internal static Clip Copy(Clip clip, string newId, Flicks at) => clip with
    {
        Id = newId,
        Range = new TimeRange(at, clip.Duration),
        LinkGroupId = null,
        GroupId = null,
        Effects = new EquatableArray<Effect>(
            clip.Effects.Select(effect => effect with { Id = Id.New() }).ToArray()),
        Markers = new EquatableArray<Marker>(
            clip.Markers.Select(marker => marker with { Id = Id.New() }).ToArray()),
    };
}

/// <summary>Puts a copy of a clip immediately after it.</summary>
public sealed class DuplicateClipHandler : ICommandHandler<DuplicateClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, DuplicateClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        string newId = HandlerHelp.IdOr(command.NewClipId);
        HandlerHelp.RequireUnused(project, newId);

        Clip copy = CopyClipHandler.Copy(found.Clip, newId, found.Clip.End);

        if (EditOps.Overlaps(found.Track, copy))
        {
            throw new CommandException(
                "would-overlap",
                $"'{found.Clip.Name}' is followed immediately by another clip, so there is no room for a copy.");
        }

        context.Changed(newId);
        context.Changed(found.Track.Id);
        return project.ReplaceTrack(found.Track.AddClip(copy));
    }
}

/// <summary>Stretches a clip to a new duration by changing its speed.</summary>
public sealed class RateStretchClipHandler : ICommandHandler<RateStretchClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RateStretchClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        Track updated = HandlerContext.Require(
            EditOps.RateStretch(found.Track, command.ClipId, command.ToDuration));

        context.Changed(command.ClipId);
        context.Changed(found.Track.Id);
        return project.ReplaceTrack(updated);
    }
}

/// <summary>Moves clips by whole frames, together or not at all.</summary>
public sealed class NudgeClipsHandler : ICommandHandler<NudgeClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, NudgeClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string[] ids = [.. command.ClipIds.Distinct(StringComparer.Ordinal)];
        if (ids.Length == 0)
        {
            throw new CommandException("no-clips", "Name at least one clip to nudge.");
        }

        ClipLocation[] found = [.. ids.Select(id => HandlerHelp.Clip(project, id))];
        Sequence sequence = found[0].Sequence;

        if (found.Any(location => !string.Equals(location.Sequence.Id, sequence.Id, StringComparison.Ordinal)))
        {
            throw new CommandException("mixed-sequences", "Clips nudged together have to be in one sequence.");
        }

        if (command.Frames == 0)
        {
            return project;
        }

        foreach (ClipLocation location in found)
        {
            HandlerHelp.RequireUnlocked(location.Track);
        }

        Flicks delta = Flicks.FromFrames(command.Frames, project.SettingsFor(sequence).FrameRate);

        // Front first, in the direction of travel: moving later, the last clip goes first, so no
        // clip lands on a neighbour that is about to move out of its way.
        IEnumerable<ClipLocation> order = command.Frames > 0
            ? found.OrderByDescending(location => location.Clip.Start)
            : found.OrderBy(location => location.Clip.Start);

        foreach (ClipLocation location in order)
        {
            Flicks to = location.Clip.Start + delta;
            if (to.IsNegative)
            {
                throw new CommandException(
                    "before-start",
                    $"Nudging '{location.Clip.Name}' {command.Frames} frame(s) would put it before the start of the sequence.");
            }

            sequence = HandlerContext.Require(EditOps.Move(sequence, location.Clip.Id, null, to));
            context.Changed(location.Clip.Id);
            context.Changed(location.Track.Id);
        }

        return project.ReplaceSequence(sequence);
    }
}
