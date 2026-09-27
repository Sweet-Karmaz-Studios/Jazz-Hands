using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;

namespace JazzHands.Engine.Handlers;

/// <summary>Enables or disables a clip.</summary>
public sealed class SetClipEnabledHandler : ICommandHandler<SetClipEnabledCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipEnabledCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        if (found.Clip.Enabled == command.Enabled)
        {
            return project;
        }

        context.Changed(command.ClipId);
        return project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { Enabled = command.Enabled }));
    }
}

/// <summary>Plays a clip backwards, or forwards again.</summary>
public sealed class SetClipReverseHandler : ICommandHandler<SetClipReverseCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipReverseCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        if (found.Clip.Reverse == command.Reverse)
        {
            return project;
        }

        context.Changed(command.ClipId);
        return project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { Reverse = command.Reverse }));
    }
}

/// <summary>Keeps a clip's pitch at its speed, or lets it follow the speed (Phase 36).</summary>
public sealed class SetClipKeepPitchHandler : ICommandHandler<SetClipKeepPitchCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipKeepPitchCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        if (found.Clip.KeepsPitch == command.Keep)
        {
            return project;
        }

        context.Changed(command.ClipId);
        return project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { PitchFollowsSpeed = command.Keep ? null : true }));
    }
}

/// <summary>Blurs a clip's picture with its speed, or stops.</summary>
public sealed class SetClipSpeedBlurHandler : ICommandHandler<SetClipSpeedBlurCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipSpeedBlurCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        if (command.On && found.Track.Kind == TrackKind.Audio)
        {
            throw new CommandException("not-a-picture", "Speed blur is for a picture; this clip is on a sound track.");
        }

        bool? on = command.On ? true : null;
        if (found.Clip.BlurFollowsSpeed == on)
        {
            return project;
        }

        context.Changed(command.ClipId);
        return project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { BlurFollowsSpeed = on }));
    }
}

/// <summary>Silences a clip's sound where it plays faster than a speed, or stops.</summary>
public sealed class SetClipFastMuteHandler : ICommandHandler<SetClipFastMuteCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipFastMuteCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);
        if (command.Off && command.Above is not null)
        {
            throw new CommandException("conflicting-options", "Give --above or --off, not both.", "above");
        }

        Rational? limit = command.Off ? null : command.Above ?? new Rational(2, 1);
        if (limit is { } speed && (speed.Num <= 0 || speed.Den <= 0))
        {
            throw new CommandException("bad-speed", "The speed to silence above must be more than zero.", "above");
        }

        if (found.Clip.MuteFasterThan == limit)
        {
            return project;
        }

        context.Changed(command.ClipId);
        return project.ReplaceTrack(found.Track.ReplaceClip(found.Clip with { MuteFasterThan = limit }));
    }
}

/// <summary>Changes how fast a clip plays.</summary>
public sealed class SetClipSpeedHandler : ICommandHandler<SetClipSpeedCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetClipSpeedCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        if (found.Clip.IsRemapped)
        {
            throw new CommandException("clip-remapped", "The clip's speed is a curve (time remap). Change its remap keyframes, or turn remap off (clip.set-remap --off) first.");
        }

        if (command.Speed.IsZero)
        {
            throw new CommandException("zero-speed", "A clip at zero speed would never advance.");
        }

        if (command.Speed.Num < 0)
        {
            throw new CommandException(
                "negative-speed",
                "Use 'jazz clip set-reverse' to play a clip backwards; speed is how fast, not which way.");
        }

        Clip clip = found.Clip;
        bool? pitchFollows = command.KeepPitch is { } keep ? (keep ? null : true) : clip.PitchFollowsSpeed;

        if (command.KeepDuration)
        {
            // The clip keeps its place and shows more or less of the source instead.
            Clip kept = clip with { Speed = command.Speed, PitchFollowsSpeed = pitchFollows };
            if (clip.EffectiveSpeed == command.Speed && clip.PitchFollowsSpeed == pitchFollows)
            {
                return project;
            }

            context.Changed(command.ClipId);
            return project.ReplaceTrack(found.Track.ReplaceClip(kept));
        }

        // The clip keeps what it shows, so its timeline duration changes to suit the new rate.
        Flicks newDuration = clip.DurationForSpeed(command.Speed);

        if (newDuration <= Flicks.Zero)
        {
            throw new CommandException("empty-result", "That speed would leave the clip with no duration.");
        }

        Clip updated = clip with { Speed = command.Speed, Range = new TimeRange(clip.Start, newDuration), PitchFollowsSpeed = pitchFollows };

        if (updated == clip)
        {
            return project;
        }

        Track track = found.Track.ReplaceClip(updated);

        if (EditOps.Overlaps(track, updated))
        {
            throw new CommandException(
                "would-overlap",
                "That speed would make the clip run into its neighbour. Use --keep-duration, or make room first.");
        }

        context.Changed(command.ClipId);
        return project.ReplaceTrack(track);
    }
}

/// <summary>Links clips so that moving one moves them all.</summary>
public sealed class LinkClipsHandler : ICommandHandler<LinkClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, LinkClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string id = HandlerHelp.IdOr(command.LinkGroupId);
        return ClipGrouping.Apply(project, command.ClipIds, context, 2, "link", clip => clip with { LinkGroupId = id });
    }
}

/// <summary>Breaks the sync lock on clips.</summary>
public sealed class UnlinkClipsHandler : ICommandHandler<UnlinkClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, UnlinkClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return ClipGrouping.Apply(project, command.ClipIds, context, 1, "unlink", clip => clip with { LinkGroupId = null });
    }
}

/// <summary>Groups clips so that selecting one selects them all.</summary>
public sealed class GroupClipsHandler : ICommandHandler<GroupClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, GroupClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string id = HandlerHelp.IdOr(command.GroupId);
        return ClipGrouping.Apply(project, command.ClipIds, context, 2, "group", clip => clip with { GroupId = id });
    }
}

/// <summary>Takes clips out of their selection group.</summary>
public sealed class UngroupClipsHandler : ICommandHandler<UngroupClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, UngroupClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return ClipGrouping.Apply(project, command.ClipIds, context, 1, "ungroup", clip => clip with { GroupId = null });
    }
}

/// <summary>The part linking and grouping have in common.</summary>
internal static class ClipGrouping
{
    /// <summary>Applies a change to every named clip, checking them all before changing any.</summary>
    internal static Project Apply(
        Project project,
        EquatableArray<string> clipIds,
        HandlerContext context,
        int minimum,
        string verb,
        Func<Clip, Clip> change)
    {
        ArgumentNullException.ThrowIfNull(project);

        string[] unique = [.. clipIds.Distinct(StringComparer.Ordinal)];

        if (unique.Length < minimum)
        {
            throw new CommandException(
                "nothing-selected",
                minimum == 1
                    ? $"Name at least one clip to {verb}."
                    : $"It takes at least two clips to {verb} them.");
        }

        // Every clip is found before any is changed, so a typo in the third id does not leave the
        // first two half done.
        ClipLocation[] found = [.. unique.Select(id => HandlerHelp.Clip(project, id))];

        Project updated = project;
        bool anyChange = false;

        foreach (ClipLocation location in found)
        {
            Clip clip = location.Clip;
            Clip changed = change(clip);

            if (changed == clip)
            {
                continue;
            }

            anyChange = true;
            context.Changed(clip.Id);
            updated = updated.ReplaceTrack(updated.FindClip(clip.Id)!.Track.ReplaceClip(changed));
        }

        return anyChange ? updated : project;
    }
}

/// <summary>Moves clips into a new sequence and leaves a compound clip behind.</summary>
public sealed class NestClipsHandler : ICommandHandler<NestClipsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, NestClipsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (command.ClipIds.IsEmpty)
        {
            throw new CommandException("nothing-selected", "Nesting needs at least one clip.");
        }

        ClipLocation first = HandlerHelp.Clip(project, command.ClipIds[0]);
        Sequence sequence = first.Sequence;

        foreach (string clipId in command.ClipIds)
        {
            ClipLocation location = HandlerHelp.Clip(project, clipId);
            HandlerHelp.RequireUnlocked(location.Track);

            if (!string.Equals(location.Sequence.Id, sequence.Id, StringComparison.Ordinal))
            {
                throw new CommandException(
                    "different-sequences",
                    "Every clip in a nest has to be in the same sequence.");
            }
        }

        if (command.NewClipId is { Length: > 0 })
        {
            HandlerHelp.RequireUnused(project, CommandValues.ParseId(command.NewClipId));
        }

        NestResult nest = HandlerContext.Require(
            EditOps.NestSelection(sequence, command.ClipIds.Items, command.Name, project.SettingsFor(sequence)));

        Sequence host = nest.Sequence;
        string compoundId = nest.CompoundClip.Id;

        // EditOps gives the compound clip an identifier of its own. Honour the one the caller
        // asked for, by swapping it on the track EditOps put it on.
        if (command.NewClipId is { Length: > 0 } wanted && !string.Equals(wanted, compoundId, StringComparison.Ordinal))
        {
            Track holder = host.TrackOf(compoundId)!;
            host = host.ReplaceTrack(
                holder.RemoveClip(compoundId).AddClip(nest.CompoundClip with { Id = wanted }));
            compoundId = wanted;
        }

        context.Changed(command.ClipIds);
        context.Changed(compoundId);
        context.Changed(nest.Nested.Id);
        context.Changed(sequence.Id);

        return project.ReplaceSequence(host).AddSequence(nest.Nested);
    }
}

/// <summary>Replaces a compound clip with the clips inside it.</summary>
public sealed class UnnestClipHandler : ICommandHandler<UnnestClipCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, UnnestClipCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ClipLocation found = HandlerHelp.Clip(project, command.ClipId);
        HandlerHelp.RequireUnlocked(found.Track);

        if (found.Clip.SequenceId is not { } nestedId)
        {
            throw new CommandException(
                "not-a-compound",
                $"'{found.Clip.Name}' is not a compound clip, so there is nothing inside it.");
        }

        Sequence nested = project.Sequence(nestedId)
            ?? throw new CommandException("missing-sequence-reference", $"No sequence with id '{nestedId}'.");

        Sequence host = found.Sequence;
        Flicks offset = found.Clip.Start;

        // The compound clip goes first, so the clips coming out of it are not checked against the
        // space it was occupying.
        Sequence working = host.ReplaceTrack(found.Track.RemoveClip(command.ClipId));
        var placed = ImmutableArray.CreateBuilder<string>();

        foreach (Track source in nested.Tracks.OrderBy(track => track.Order))
        {
            if (source.Clips.IsEmpty)
            {
                continue;
            }

            Track destination = Destination(working, nested, source, found.Track)
                ?? throw new CommandException(
                    "no-room",
                    $"'{host.Name}' has no {source.Kind.ToString().ToLowerInvariant()} track to put '{source.Name}' back on.");

            foreach (Clip inner in source.Clips)
            {
                Clip moved = inner with { Range = new TimeRange(inner.Start + offset, inner.Duration) };

                if (EditOps.Overlaps(destination, moved))
                {
                    throw new CommandException(
                        "would-overlap",
                        $"'{inner.Name}' would land on top of a clip already on '{destination.Name}'.");
                }

                destination = destination.AddClip(moved);
                placed.Add(moved.Id);
            }

            working = working.ReplaceTrack(destination);
        }

        Project updated = project.ReplaceSequence(working);

        // The nested sequence goes too, unless something else still nests it.
        if (!IsUsed(updated, nestedId))
        {
            updated = updated.RemoveSequence(nestedId);
            context.Changed(nestedId);
        }

        context.Changed(command.ClipId);
        context.Changed(placed);
        context.Changed(host.Id);

        return updated;
    }

    /// <summary>The track in the host that a nested track should come back to.</summary>
    /// <remarks>
    /// Matched by kind and then by place among tracks of that kind. Tracks of the compound clip's
    /// own kind count up from the track it was on, so a nest of the clips on a title track comes
    /// back onto that track rather than onto the first track of its kind; the others count from the
    /// host's first. Past the last track of a kind, the last one takes them.
    /// </remarks>
    private static Track? Destination(Sequence host, Sequence nested, Track source, Track anchor)
    {
        Track[] candidates = [.. host.Tracks.Where(track => track.Kind == source.Kind).OrderBy(track => track.Order)];
        if (candidates.Length == 0)
        {
            return null;
        }

        int place = nested.Tracks.Where(track => track.Kind == source.Kind).OrderBy(track => track.Order).TakeWhile(track => track.Id != source.Id).Count();
        int start = anchor.Kind == source.Kind ? Math.Max(0, Array.FindIndex(candidates, track => track.Id == anchor.Id)) : 0;
        return candidates[Math.Min(start + place, candidates.Length - 1)];
    }

    private static bool IsUsed(Project project, string sequenceId) =>
        project.Sequences.Any(sequence => sequence.Tracks.Any(track =>
            track.Clips.Any(clip => string.Equals(clip.SequenceId, sequenceId, StringComparison.Ordinal))));
}
