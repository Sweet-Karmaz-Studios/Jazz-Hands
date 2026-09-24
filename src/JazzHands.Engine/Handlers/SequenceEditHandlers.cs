using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>What the handlers for sequence-wide edits share.</summary>
internal static class SequenceEdit
{
    /// <summary>The sequence the first of some clips is in; the edit itself checks the rest.</summary>
    internal static Sequence Of(Project project, IReadOnlyList<string> clipIds, string what)
    {
        if (clipIds.Count == 0)
        {
            throw new CommandException("nothing-selected", $"{what} needs a clip.");
        }

        Sequence sequence = HandlerHelp.Clip(project, clipIds[0]).Sequence;
        RefuseQuickTrim(sequence);
        return sequence;
    }

    /// <summary>Puts the edited sequence in the project and reports every clip and track that changed.</summary>
    internal static Project Commit(Project project, Sequence before, Sequence after, HandlerContext context)
    {
        if (ReferenceEquals(before, after))
        {
            return project;
        }

        RefuseQuickTrim(before);
        context.Changed(EditOps.Changed(before, after));
        return project.ReplaceSequence(after);
    }

    /// <summary>
    /// Refuses a sequence-wide edit of a Quick Trim, whose clips sit at their own source times:
    /// a ripple, an insert or a paste would move them off, and it would stop being a trim of its
    /// file without saying so.
    /// </summary>
    internal static void RefuseQuickTrim(Sequence sequence)
    {
        if (sequence.QuickTrim is not null)
        {
            throw new CommandException(
                "quick-trim",
                $"'{sequence.Name}' is a Quick Trim, which keeps every clip at its place in the file. Cut it with trim.remove-range, or nest it in another sequence to edit it freely.");
        }
    }

    /// <summary>How much source each clip has, so a trim cannot run off the end of it.</summary>
    internal static Func<Clip, Flicks?> SourceLength(Project project) =>
        clip => SlipClipHandler.SourceDuration(project, clip);
}

/// <summary>Trims a clip edge and ripples everything after it.</summary>
public sealed class RippleTrimClipsHandler : ICommandHandler<RippleTrimClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RippleTrimClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.Edge == ClipEdge.None)
        {
            throw new CommandException("invalid-edge", "Say which edge: --edge start or --edge end.");
        }

        Sequence sequence = SequenceEdit.Of(project, command.ClipIds, "A ripple trim");
        Sequence after = HandlerContext.Require(
            EditOps.RippleTrim(sequence, command.ClipIds, command.Edge, command.To, SequenceEdit.SourceLength(project)));

        return SequenceEdit.Commit(project, sequence, after, context);
    }
}

/// <summary>Removes clips and closes the gap on every sync-locked track.</summary>
public sealed class RippleDeleteClipsHandler : ICommandHandler<RippleDeleteClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RippleDeleteClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = SequenceEdit.Of(project, command.ClipIds, "A ripple delete");
        Sequence after = HandlerContext.Require(EditOps.RippleDelete(sequence, command.ClipIds));
        return SequenceEdit.Commit(project, sequence, after, context);
    }
}

/// <summary>Moves clips along the primary track.</summary>
public sealed class StorylineMoveClipsHandler : ICommandHandler<StorylineMoveClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, StorylineMoveClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = SequenceEdit.Of(project, command.ClipIds, "A storyline move");
        Sequence after = HandlerContext.Require(EditOps.MoveOnStoryline(sequence, command.ClipIds, command.To));
        return SequenceEdit.Commit(project, sequence, after, context);
    }
}

/// <summary>Closes a gap on a track.</summary>
public sealed class CloseGapHandler : ICommandHandler<CloseGapCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, CloseGapCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);
        SequenceEdit.RefuseQuickTrim(sequence);
        Sequence after = HandlerContext.Require(EditOps.CloseGap(sequence, track.Id, command.At));
        return SequenceEdit.Commit(project, sequence, after, context);
    }
}

/// <summary>Removes a range, leaving a gap.</summary>
public sealed class LiftRangeHandler : ICommandHandler<LiftRangeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, LiftRangeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        SequenceEdit.RefuseQuickTrim(sequence);
        Sequence after = HandlerContext.Require(EditOps.LiftRange(
            sequence,
            TimeRange.FromBounds(command.From, Flicks.Max(command.From, command.To)),
            command.TrackIds.IsEmpty ? null : command.TrackIds));

        return SequenceEdit.Commit(project, sequence, after, context);
    }
}

/// <summary>Removes a range and closes the gap.</summary>
public sealed class ExtractRangeHandler : ICommandHandler<ExtractRangeCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ExtractRangeCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        SequenceEdit.RefuseQuickTrim(sequence);
        Sequence after = HandlerContext.Require(EditOps.ExtractRange(
            sequence,
            TimeRange.FromBounds(command.From, Flicks.Max(command.From, command.To)),
            command.TrackIds.IsEmpty ? null : command.TrackIds));

        return SequenceEdit.Commit(project, sequence, after, context);
    }
}

/// <summary>Puts a clip in at a time, pushing the rest on.</summary>
public sealed class InsertClipHandler : ICommandHandler<InsertClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, InsertClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);
        SequenceEdit.RefuseQuickTrim(sequence);
        HandlerHelp.RequireUnlocked(track);

        AddClipCommand add = command.ToAdd();
        Flicks duration = AddClipHandler.DurationOf(project, add);

        // A clip that cannot go in is refused by the add, with the add's words for why.
        if (duration > Flicks.Zero && add.At >= Flicks.Zero)
        {
            Sequence opened = HandlerContext.Require(EditOps.Ripple(sequence, add.At, duration, [track.Id]));
            project = SequenceEdit.Commit(project, sequence, opened, context);
        }

        return new AddClipHandler().Handle(project, add, context);
    }
}

/// <summary>Puts a clip over whatever is at a time.</summary>
public sealed class OverwriteClipHandler : ICommandHandler<OverwriteClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, OverwriteClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);
        SequenceEdit.RefuseQuickTrim(sequence);
        HandlerHelp.RequireUnlocked(track);

        AddClipCommand add = command.ToAdd();
        Flicks duration = AddClipHandler.DurationOf(project, add);

        if (duration > Flicks.Zero && add.At >= Flicks.Zero)
        {
            string[] tracks = [track.Id, .. AddClipHandler.LinkedAudioTracks(project, sequence, add, track)];
            Sequence cleared = HandlerContext.Require(EditOps.LiftRange(sequence, new TimeRange(add.At, duration), tracks));
            project = SequenceEdit.Commit(project, sequence, cleared, context);
        }

        return new AddClipHandler().Handle(project, add, context);
    }
}

/// <summary>Holds a frame for a while.</summary>
public sealed class FreezeFrameHandler : ICommandHandler<FreezeFrameCommand>
{
    /// <summary>How long a freeze frame lasts when the command does not say.</summary>
    public static readonly Flicks DefaultDuration = Flicks.FromSeconds(2);

    /// <inheritdoc />
    public Project Handle(Project project, FreezeFrameCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        SequenceEdit.RefuseQuickTrim(found.Sequence);

        string holdId = HandlerHelp.IdOr(command.NewClipId);
        HandlerHelp.RequireUnused(project, holdId);

        Sequence after = HandlerContext.Require(EditOps.FreezeFrame(
            found.Sequence,
            command.ClipId,
            command.At,
            command.Duration ?? DefaultDuration,
            holdId));

        return SequenceEdit.Commit(project, found.Sequence, after, context);
    }
}

/// <summary>Pastes copied clips.</summary>
public sealed class PasteClipsHandler : ICommandHandler<PasteClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, PasteClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        SequenceEdit.RefuseQuickTrim(sequence);
        ClipboardContent content = HandlerContext.Require(ClipboardOps.FromJson(command.Data));

        Project after = HandlerContext.Require(ClipboardOps.Paste(
            project,
            sequence.Id,
            content,
            command.At,
            command.TrackId,
            command.Insert,
            item => HandlerHelp.Resolve(context, item.RelativePath),
            path => HandlerHelp.Store(context, path),
            Id.New));

        context.Changed(after.Media.Select(item => item.Id).Except(project.Media.Select(item => item.Id), StringComparer.Ordinal));
        context.Changed(EditOps.Changed(sequence, after.Sequence(sequence.Id)!));
        return after;
    }
}

/// <summary>Turns magnetic mode on or off; the dispatcher closes the gaps.</summary>
public sealed class SetTimelineMagneticHandler : ICommandHandler<SetTimelineMagneticCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTimelineMagneticCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence = HandlerHelp.Sequence(project, command.SequenceId);
        if (sequence.IsMagnetic == command.Magnetic)
        {
            return project;
        }

        SequenceEdit.RefuseQuickTrim(sequence);

        context.Changed(sequence.Id);
        return project.ReplaceSequence(sequence with { Magnetic = command.Magnetic ? true : null });
    }
}

/// <summary>Turns sync lock on or off for a track.</summary>
public sealed class SetTrackSyncLockHandler : ICommandHandler<SetTrackSyncLockCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTrackSyncLockCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence _, Track track) = HandlerHelp.Track(project, command.TrackId);
        if (track.IsSyncLocked == command.SyncLocked)
        {
            return project;
        }

        // On is the default, and the default is left out of the file.
        context.Changed(track.Id);
        return project.ReplaceTrack(track with { SyncLock = command.SyncLocked ? null : false });
    }
}

/// <summary>Copies clips as JSON for <c>clip.paste</c>.</summary>
public sealed class CopyClipsHandler : IQueryHandler<CopyClipsQuery, string>
{
    /// <inheritdoc />
    public string Handle(Project project, CopyClipsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        string projectPath = context.Session?.ProjectPath ?? string.Empty;
        ClipboardContent content = HandlerContext.Require(ClipboardOps.Copy(
            project,
            query.ClipIds,
            item => HandlerHelp.Resolve(projectPath, item.RelativePath)));

        return ClipboardOps.ToJson(content);
    }
}

/// <summary>Finds the source frame a clip shows at a time.</summary>
public sealed class MatchFrameHandler : IQueryHandler<MatchFrameQuery, MatchFrameInfo>
{
    /// <inheritdoc />
    public MatchFrameInfo Handle(Project project, MatchFrameQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        Clip clip = HandlerHelp.Clip(project, query.ClipId).Clip;
        if (!clip.Range.Contains(query.At))
        {
            throw new CommandException(
                "time-out-of-range",
                $"'{clip.Name}' runs from {Timecode.FormatClock(clip.Start)} to {Timecode.FormatClock(clip.End)}; {Timecode.FormatClock(query.At)} is not in it.");
        }

        if (clip.MediaId is null && clip.SequenceId is null)
        {
            throw new CommandException("no-source", $"'{clip.Name}' is generated, so there is no source frame to match.");
        }

        string projectPath = context.Session?.ProjectPath ?? string.Empty;
        string path = clip.MediaId is { } mediaId && project.MediaItem(mediaId) is { } media
            ? HandlerHelp.Resolve(projectPath, media.RelativePath)
            : string.Empty;

        return new MatchFrameInfo(clip.Id, clip.MediaId, clip.SequenceId, clip.SourceTimeAt(query.At), path);
    }
}
