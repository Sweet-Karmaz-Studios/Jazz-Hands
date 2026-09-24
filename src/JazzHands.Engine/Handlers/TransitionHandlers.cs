using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Time;
using JazzHands.Core.Validation;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Effects;

namespace JazzHands.Engine.Handlers;

/// <summary>What the transition handlers share: finding one, choosing a type, fitting, handles and links.</summary>
internal static class TransitionHelp
{
    /// <summary>How far from a time <c>transition.apply-default</c> looks for a cut.</summary>
    internal static readonly Flicks CutReach = Flicks.OneSecond;

    /// <summary>A transition by id, with its sequence and track, or a coded refusal.</summary>
    internal static (Sequence Sequence, Track Track, Transition Transition) Find(Project project, string transitionId)
    {
        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Track track in sequence.Tracks)
            {
                foreach (Transition transition in track.Transitions)
                {
                    if (string.Equals(transition.Id, transitionId, StringComparison.Ordinal))
                    {
                        return (sequence, track, transition);
                    }
                }
            }
        }

        throw new CommandException(
            "transition-not-found",
            $"No transition with id '{transitionId}'. 'jazz transition list' shows them.",
            "/sequences");
    }

    /// <summary>The kind of transition a track takes, or a refusal for a track that takes none.</summary>
    internal static EffectKind KindFor(Track track) => track.Kind switch
    {
        TrackKind.Video => EffectKind.Transition,
        TrackKind.Audio => EffectKind.AudioTransition,
        _ => throw new CommandException(
            "transition-wrong-track",
            $"'{track.Name}' is not a picture or a sound track. Transitions go between clips on those."),
    };

    /// <summary>
    /// The type a transition on a track is to be: the one asked for, which must suit the track,
    /// or the project's default for the track.
    /// </summary>
    internal static EffectDescriptor Type(string? typeId, Track track, ProjectSettings settings)
    {
        EffectKind kind = KindFor(track);
        TransitionDefaults defaults = settings.EffectiveTransitions;
        string id = typeId is { Length: > 0 } ? typeId : kind == EffectKind.Transition ? defaults.Video : defaults.Audio;
        EffectDescriptor descriptor = EffectCatalog.Registry.Require(id);

        if (descriptor.Kind != kind)
        {
            string wanted = kind == EffectKind.Transition ? "a picture transition" : "a sound transition";
            throw new CommandException(
                "wrong-transition-kind",
                descriptor.Kind is EffectKind.Transition or EffectKind.AudioTransition
                    ? $"'{id}' is not {wanted}, which is what '{track.Name}' takes."
                    : $"'{id}' is not a transition. 'jazz effect list' shows the transitions under the kinds Transition and AudioTransition.");
        }

        return descriptor;
    }

    /// <summary>A duration a command gave, checked, or the project's default.</summary>
    internal static Flicks Duration(Flicks? given, ProjectSettings settings)
    {
        Flicks duration = given ?? settings.EffectiveTransitions.Duration;
        if (duration.Value <= 0)
        {
            throw new CommandException("invalid-duration", "A transition needs a duration longer than zero.");
        }

        return duration;
    }

    /// <summary>The two clips a cut joins, checked: on one track, next to each other, the track unlocked.</summary>
    internal static (Sequence Sequence, Track Track, Clip Left, Clip Right) Cut(Project project, string leftClipId, string rightClipId)
    {
        ClipLocation left = HandlerHelp.Clip(project, leftClipId);
        ClipLocation right = HandlerHelp.Clip(project, rightClipId);

        if (!string.Equals(left.Track.Id, right.Track.Id, StringComparison.Ordinal))
        {
            throw new CommandException(
                "not-adjacent",
                $"'{left.Clip.Name}' and '{right.Clip.Name}' are on different tracks. A transition joins two clips on one track.");
        }

        if (left.Clip.End != right.Clip.Start)
        {
            throw new CommandException(
                "not-adjacent",
                right.Clip.End == left.Clip.Start
                    ? $"'{right.Clip.Name}' comes before '{left.Clip.Name}'. Name the outgoing clip first."
                    : $"'{left.Clip.Name}' ends at {Timecode.FormatClock(left.Clip.End)} and '{right.Clip.Name}' starts at "
                      + $"{Timecode.FormatClock(right.Clip.Start)}. A transition needs them to meet at a cut.");
        }

        HandlerHelp.RequireUnlocked(left.Track);
        KindFor(left.Track);
        return (left.Sequence, left.Track, left.Clip, right.Clip);
    }

    /// <summary>
    /// The duration a transition gets on a cut: what was asked for, shortened to what the clips
    /// have room for. Centred, both halves fit, so it stays on the cut.
    /// </summary>
    internal static Flicks Fit(Track track, Clip left, Clip right, Flicks duration, TransitionAlignment alignment, string? ignoreId, Rational frameRate)
    {
        // The room on each side: the clip, less what the transition at its other end takes.
        Flicks leftRoom = left.Duration - Taken(track, left, atStart: true, ignoreId, frameRate);
        Flicks rightRoom = right.Duration - Taken(track, right, atStart: false, ignoreId, frameRate);

        Flicks fitted = alignment switch
        {
            TransitionAlignment.EndOfLeft => Flicks.Min(duration, leftRoom),
            TransitionAlignment.StartOfRight => Flicks.Min(duration, rightRoom),
            _ => FitCentred(duration, leftRoom, rightRoom, frameRate),
        };

        if (fitted.Value <= 0)
        {
            throw new CommandException(
                "no-room",
                $"'{left.Name}' and '{right.Name}' leave no room for a transition there: another one already takes the clip.");
        }

        return fitted;
    }

    /// <summary>
    /// Adds a transition to a cut, or refuses, trims or holds when a clip has too little source
    /// past the cut. Returns the project and the cut as it ends up, which a trim moves.
    /// </summary>
    internal static (Project Project, Clip Left, Clip Right) Add(
        Project project,
        string trackId,
        Clip left,
        Clip right,
        EffectDescriptor type,
        Flicks duration,
        TransitionAlignment alignment,
        TransitionHandles handles,
        string id,
        HandlerContext context)
    {
        (Sequence sequence, Track track) = HandlerHelp.Track(project, trackId);
        Rational frameRate = project.SettingsFor(sequence).FrameRate;

        if (TransitionTiming.Between(track, left.Id, right.Id) is { } existing)
        {
            throw new CommandException(
                "transition-exists",
                $"The cut between '{left.Name}' and '{right.Name}' already has a transition, '{existing.Id}'. Change it with 'jazz transition set {existing.Id}'.");
        }

        Flicks fitted = Fit(track, left, right, duration, alignment, ignoreId: null, frameRate);
        var transition = new Transition(id, type.TypeId, left.Id, right.Id, fitted, alignment, EquatableArray<EffectParameter>.Empty);
        Track withTransition = track with { Transitions = track.Transitions.Add(transition) };
        TransitionSpan span = TransitionTiming.Span(withTransition, transition, frameRate)!.Value;
        (Flicks leftShort, Flicks rightShort) = TransitionTiming.Shortfall(project, span);

        if (leftShort.Value > 0 || rightShort.Value > 0)
        {
            switch (handles)
            {
                case TransitionHandles.Refuse:
                    throw new CommandException("insufficient-handles", Validator.ShortHandles(left, right, leftShort, rightShort));

                case TransitionHandles.Trim:
                    (project, left, right) = TrimForHandles(project, sequence, left, right, leftShort, rightShort, context);
                    (sequence, track) = HandlerHelp.Track(project, trackId);
                    transition = transition with { Duration = Fit(track, left, right, duration, alignment, ignoreId: null, frameRate) };
                    withTransition = track with { Transitions = track.Transitions.Add(transition) };
                    span = TransitionTiming.Span(withTransition, transition, frameRate)!.Value;
                    (leftShort, rightShort) = TransitionTiming.Shortfall(project, span);
                    if (leftShort.Value > 0 || rightShort.Value > 0)
                    {
                        throw new CommandException(
                            "insufficient-handles",
                            $"Trimming '{left.Name}' and '{right.Name}' back still leaves too little source: {Validator.ShortHandles(left, right, leftShort, rightShort)}");
                    }

                    break;
            }
        }

        context.Changed(transition.Id);
        context.Changed(track.Id);
        return (project.ReplaceTrack(withTransition), left, right);
    }

    /// <summary>
    /// Trims the outgoing clip's end and the incoming clip's start back from the cut by what each
    /// is short, with the clips linked to each that share the edge, rippling what follows.
    /// </summary>
    private static (Project Project, Clip Left, Clip Right) TrimForHandles(
        Project project,
        Sequence sequence,
        Clip left,
        Clip right,
        Flicks leftShort,
        Flicks rightShort,
        HandlerContext context)
    {
        Sequence result = sequence;

        if (leftShort.Value > 0)
        {
            string[] ids = SharingEdge(result, left, ClipEdge.End);
            result = HandlerContext.Require(EditOps.RippleTrim(result, ids, ClipEdge.End, left.End - leftShort));
        }

        if (rightShort.Value > 0)
        {
            Clip moved = result.TrackOf(right.Id)!.Clip(right.Id)!;
            string[] ids = SharingEdge(result, moved, ClipEdge.Start);
            result = HandlerContext.Require(EditOps.RippleTrim(result, ids, ClipEdge.Start, moved.Start + rightShort));
        }

        context.Changed(EditOps.Changed(sequence, result));
        Clip newLeft = result.TrackOf(left.Id)!.Clip(left.Id)!;
        Clip newRight = result.TrackOf(right.Id)!.Clip(right.Id)!;
        return (project.ReplaceSequence(result), newLeft, newRight);
    }

    /// <summary>A clip and the clips linked to it whose edge is at the same time, as a ripple trim takes them.</summary>
    private static string[] SharingEdge(Sequence sequence, Clip clip, ClipEdge edge)
    {
        if (clip.LinkGroupId is null)
        {
            return [clip.Id];
        }

        return [.. TimelineQueries.LinkedClips(sequence, clip.Id)
            .Where(other => edge == ClipEdge.End ? other.End == clip.End : other.Range == clip.Range)
            .Select(other => other.Id)];
    }

    /// <summary>
    /// The cuts on other tracks between clips linked to these two, at the same time: where a
    /// picture's sound turns over with it.
    /// </summary>
    internal static IEnumerable<(Track Track, Clip Left, Clip Right)> LinkedCuts(Sequence sequence, Track track, Clip left, Clip right)
    {
        if (left.LinkGroupId is null || right.LinkGroupId is null)
        {
            yield break;
        }

        foreach (Track other in sequence.Tracks)
        {
            if (string.Equals(other.Id, track.Id, StringComparison.Ordinal) || other.Kind is not (TrackKind.Video or TrackKind.Audio))
            {
                continue;
            }

            Clip? outgoing = null;
            Clip? incoming = null;
            foreach (Clip clip in other.Clips)
            {
                if (clip.End == left.End && string.Equals(clip.LinkGroupId, left.LinkGroupId, StringComparison.Ordinal))
                {
                    outgoing = clip;
                }
                else if (clip.Start == right.Start && string.Equals(clip.LinkGroupId, right.LinkGroupId, StringComparison.Ordinal))
                {
                    incoming = clip;
                }
            }

            if (outgoing is not null && incoming is not null)
            {
                yield return (other, outgoing, incoming);
            }
        }
    }

    /// <summary>The transitions at the linked cuts of a transition's cut.</summary>
    internal static IEnumerable<(Track Track, Transition Transition)> Partners(Sequence sequence, Track track, Transition transition)
    {
        if (track.Clip(transition.LeftClipId) is not { } left || track.Clip(transition.RightClipId) is not { } right)
        {
            yield break;
        }

        foreach ((Track other, Clip outgoing, Clip incoming) in LinkedCuts(sequence, track, left, right))
        {
            if (TransitionTiming.Between(other, outgoing.Id, incoming.Id) is { } partner)
            {
                yield return (other, partner);
            }
        }
    }

    /// <summary>
    /// Adds the default sound crossfade at each linked cut of a picture transition that has none,
    /// with the same duration and alignment. A sound clip short of source holds rather than refuses.
    /// </summary>
    internal static Project AddLinkedAudio(Project project, string trackId, Transition picture, HandlerContext context)
    {
        (Sequence sequence, Track track) = HandlerHelp.Track(project, trackId);
        Clip left = track.Clip(picture.LeftClipId)!;
        Clip right = track.Clip(picture.RightClipId)!;
        ProjectSettings settings = project.SettingsFor(sequence);

        foreach ((Track other, Clip outgoing, Clip incoming) in LinkedCuts(sequence, track, left, right).ToList())
        {
            if (other.Kind != TrackKind.Audio || other.Locked || TransitionTiming.Between(other, outgoing.Id, incoming.Id) is not null)
            {
                continue;
            }

            EffectDescriptor type = Type(null, other, settings);
            (project, _, _) = Add(project, other.Id, outgoing, incoming, type, picture.Duration, picture.Alignment, TransitionHandles.Hold, Id.New(), context);
        }

        return project;
    }

    /// <summary>A transition with its duration or alignment changed, fitted to its clips.</summary>
    internal static Transition Resize(Project project, Sequence sequence, Track track, Transition transition, Flicks? duration, TransitionAlignment? alignment)
    {
        if (duration is { Value: <= 0 })
        {
            throw new CommandException("invalid-duration", "A transition needs a duration longer than zero.");
        }

        TransitionAlignment aligned = alignment ?? transition.Alignment;
        Flicks asked = duration ?? transition.Duration;
        if (track.Clip(transition.LeftClipId) is not { } left || track.Clip(transition.RightClipId) is not { } right)
        {
            return transition with { Duration = asked, Alignment = aligned };
        }

        Rational frameRate = project.SettingsFor(sequence).FrameRate;
        return transition with { Duration = Fit(track, left, right, asked, aligned, transition.Id, frameRate), Alignment = aligned };
    }

    /// <summary>A project with a transition replaced on its track.</summary>
    internal static Project Replace(Project project, string trackId, Transition transition)
    {
        (_, Track track) = HandlerHelp.Track(project, trackId);
        int index = track.Transitions.IndexOf(item => string.Equals(item.Id, transition.Id, StringComparison.Ordinal));
        return project.ReplaceTrack(track with { Transitions = track.Transitions.SetItem(index, transition) });
    }

    /// <summary>The cuts on a track: every pair of clips where one ends as the next starts.</summary>
    internal static IEnumerable<(Clip Left, Clip Right)> Cuts(Track track)
    {
        for (int index = 1; index < track.Clips.Length; index++)
        {
            if (track.Clips[index - 1].End == track.Clips[index].Start)
            {
                yield return (track.Clips[index - 1], track.Clips[index]);
            }
        }
    }

    /// <summary>A transition as a query reports it.</summary>
    internal static TransitionInfo Info(Project project, Sequence sequence, Track track, Transition transition)
    {
        Rational frameRate = project.SettingsFor(sequence).FrameRate;
        ParamOwner owner = new(ParamOwnerKind.Transition, transition.Id, sequence, track, Transition: transition);
        Clip? left = track.Clip(transition.LeftClipId);

        if (TransitionTiming.Span(track, transition, frameRate) is { } span)
        {
            (Flicks shortBefore, Flicks shortAfter) = TransitionTiming.Shortfall(project, span);
            return new TransitionInfo(
                transition.Id,
                transition.TypeId,
                EffectCatalog.Registry.Find(transition.TypeId)?.Name ?? transition.TypeId,
                track.Id,
                transition.LeftClipId,
                transition.RightClipId,
                transition.Duration,
                transition.Alignment,
                span.Cut,
                span.Range.Start,
                span.Range.End,
                span.IsClamped,
                shortBefore,
                shortAfter,
                ParamHelp.Infos(owner));
        }

        Flicks cut = left?.End ?? Flicks.Zero;
        return new TransitionInfo(
            transition.Id,
            transition.TypeId,
            EffectCatalog.Registry.Find(transition.TypeId)?.Name ?? transition.TypeId,
            track.Id,
            transition.LeftClipId,
            transition.RightClipId,
            transition.Duration,
            transition.Alignment,
            cut,
            cut,
            cut,
            Fitted: true,
            Flicks.Zero,
            Flicks.Zero,
            ParamHelp.Infos(owner));
    }

    private static Flicks FitCentred(Flicks duration, Flicks leftRoom, Flicks rightRoom, Rational frameRate)
    {
        (Flicks before, Flicks after) = TransitionTiming.Split(duration, TransitionAlignment.Centered, frameRate);
        if (before <= leftRoom && after <= rightRoom)
        {
            return duration;
        }

        // Both halves shrink together until the shorter side fits, keeping it on the cut.
        Flicks half = Flicks.Max(Flicks.Min(leftRoom, rightRoom), Flicks.Zero);
        long frames = half.ToFrames(frameRate, RoundingMode.Floor);
        Flicks whole = Flicks.FromFrames(frames, frameRate);
        return frames > 0 ? whole * 2 : half * 2;
    }

    /// <summary>How much of a clip the transition at one of its ends takes, other than the one being fitted.</summary>
    private static Flicks Taken(Track track, Clip clip, bool atStart, string? ignoreId, Rational frameRate)
    {
        foreach (Transition other in track.Transitions)
        {
            if (string.Equals(other.Id, ignoreId, StringComparison.Ordinal))
            {
                continue;
            }

            bool joins = atStart
                ? string.Equals(other.RightClipId, clip.Id, StringComparison.Ordinal)
                : string.Equals(other.LeftClipId, clip.Id, StringComparison.Ordinal);
            if (joins && TransitionTiming.Span(track, other, frameRate) is { } span)
            {
                return atStart ? span.After : span.Before;
            }
        }

        return Flicks.Zero;
    }
}

/// <summary>Puts a transition on a cut.</summary>
public sealed class AddTransitionHandler : ICommandHandler<AddTransitionCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddTransitionCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track, Clip left, Clip right) = TransitionHelp.Cut(project, command.LeftClipId, command.RightClipId);
        ProjectSettings settings = project.SettingsFor(sequence);
        EffectDescriptor type = TransitionHelp.Type(command.Type, track, settings);
        Flicks duration = TransitionHelp.Duration(command.Duration, settings);

        string id = HandlerHelp.IdOr(command.TransitionId);
        HandlerHelp.RequireUnused(project, id);

        (Project added, _, _) = TransitionHelp.Add(project, track.Id, left, right, type, duration, command.Alignment, command.Handles, id, context);

        if (command.Audio && track.Kind == TrackKind.Video)
        {
            (_, Track now, Transition transition) = TransitionHelp.Find(added, id);
            added = TransitionHelp.AddLinkedAudio(added, now.Id, transition, context);
        }

        return added;
    }
}

/// <summary>Takes a transition off its cut.</summary>
public sealed class RemoveTransitionHandler : ICommandHandler<RemoveTransitionCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, RemoveTransitionCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track, Transition transition) = TransitionHelp.Find(project, command.TransitionId);
        HandlerHelp.RequireUnlocked(track);

        var gone = new List<(Track Track, Transition Transition)> { (track, transition) };
        if (command.Linked)
        {
            gone.AddRange(TransitionHelp.Partners(sequence, track, transition).Where(partner => !partner.Track.Locked));
        }

        foreach ((Track on, Transition removed) in gone)
        {
            (_, Track current) = HandlerHelp.Track(project, on.Id);
            project = project.ReplaceTrack(current with
            {
                Transitions = new EquatableArray<Transition>(current.Transitions.Where(item => !string.Equals(item.Id, removed.Id, StringComparison.Ordinal))),
            });
            context.Changed(removed.Id);
            context.Changed(on.Id);
        }

        return project;
    }
}

/// <summary>Changes a transition's type, duration or alignment.</summary>
public sealed class SetTransitionHandler : ICommandHandler<SetTransitionCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTransitionCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track, Transition transition) = TransitionHelp.Find(project, command.TransitionId);
        HandlerHelp.RequireUnlocked(track);

        Transition changed = transition;
        if (command.Type is { Length: > 0 } typeId && !string.Equals(typeId, transition.TypeId, StringComparison.Ordinal))
        {
            EffectDescriptor type = TransitionHelp.Type(typeId, track, project.SettingsFor(sequence));

            // What the two types share, by name and kind of value, carries over.
            var kept = transition.Parameters.Where(parameter =>
                type.Param(parameter.Name) is { } wanted
                && EffectCatalog.Registry.Find(transition.TypeId)?.Param(parameter.Name) is { } had
                && wanted.Type == had.Type);
            changed = changed with { TypeId = type.TypeId, Parameters = new EquatableArray<EffectParameter>(kept) };
        }

        bool timing = command.Duration is not null || command.Alignment is not null;
        if (timing)
        {
            changed = TransitionHelp.Resize(project, sequence, track, changed, command.Duration, command.Alignment);
        }

        if (changed == transition)
        {
            return project;
        }

        List<(Track Track, Transition Transition)> partners = timing && command.Linked
            ? [.. TransitionHelp.Partners(sequence, track, transition).Where(partner => !partner.Track.Locked)]
            : [];

        project = TransitionHelp.Replace(project, track.Id, changed);
        context.Changed(changed.Id);
        context.Changed(track.Id);

        foreach ((Track on, Transition partner) in partners)
        {
            (Sequence current, Track now) = HandlerHelp.Track(project, on.Id);
            Transition follows = TransitionHelp.Resize(project, current, now, partner, command.Duration, command.Alignment);
            project = TransitionHelp.Replace(project, on.Id, follows);
            context.Changed(partner.Id);
            context.Changed(on.Id);
        }

        return project;
    }
}

/// <summary>Sets one of a transition's parameters.</summary>
public sealed class SetTransitionParamHandler : ICommandHandler<SetTransitionParamCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetTransitionParamCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        TransitionHelp.Find(project, command.TransitionId);
        ParamOwner owner = ParamHelp.Editable(project, command.TransitionId);
        return SetParamHandler.Set(project, owner, command.Param, command.Value, at: null, local: false, context);
    }
}

/// <summary>Puts the default transition on the cut nearest a time.</summary>
public sealed class ApplyDefaultTransitionHandler : ICommandHandler<ApplyDefaultTransitionCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ApplyDefaultTransitionCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Sequence sequence;
        List<Track> tracks;
        if (command.TrackId is { Length: > 0 } trackId)
        {
            (sequence, Track only) = HandlerHelp.Track(project, trackId);
            HandlerHelp.RequireUnlocked(only);
            TransitionHelp.KindFor(only);
            tracks = [only];
        }
        else
        {
            sequence = HandlerHelp.Sequence(project, command.SequenceId);
            tracks = [.. sequence.Tracks.Where(track => !track.Locked && command.Kind switch
            {
                TransitionKinds.Video => track.Kind == TrackKind.Video,
                TransitionKinds.Audio => track.Kind == TrackKind.Audio,
                _ => track.Kind is TrackKind.Video or TrackKind.Audio,
            })];
        }

        // The nearest cut on any of the tracks, then every one of them with a cut there.
        Flicks? nearest = null;
        foreach (Track track in tracks)
        {
            foreach ((Clip left, _) in TransitionHelp.Cuts(track))
            {
                Flicks distance = left.End > command.AtCut ? left.End - command.AtCut : command.AtCut - left.End;
                if (distance <= TransitionHelp.CutReach && (nearest is not { } best || distance < (best > command.AtCut ? best - command.AtCut : command.AtCut - best)))
                {
                    nearest = left.End;
                }
            }
        }

        if (nearest is not { } cut)
        {
            throw new CommandException(
                "no-cut",
                $"There is no cut within a second of {Timecode.FormatClock(command.AtCut)} on {(tracks.Count == 1 ? $"'{tracks[0].Name}'" : "those tracks")}.");
        }

        ProjectSettings settings = project.SettingsFor(sequence);
        Flicks duration = settings.EffectiveTransitions.Duration;
        foreach (Track listed in tracks)
        {
            (_, Track track) = HandlerHelp.Track(project, listed.Id);
            if (TransitionHelp.Cuts(track).FirstOrDefault(pair => pair.Left.End == cut) is not ({ } left, { } right)
                || TransitionTiming.Between(track, left.Id, right.Id) is not null)
            {
                continue;
            }

            EffectDescriptor type = TransitionHelp.Type(null, track, settings);
            (project, _, _) = TransitionHelp.Add(project, track.Id, left, right, type, duration, TransitionAlignment.Centered, command.Handles, Id.New(), context);
        }

        return project;
    }
}

/// <summary>Puts a transition on every cut of a track.</summary>
public sealed class AddAllTransitionsHandler : ICommandHandler<AddAllTransitionsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AddAllTransitionsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        (Sequence sequence, Track track) = HandlerHelp.Track(project, command.TrackId);
        HandlerHelp.RequireUnlocked(track);
        ProjectSettings settings = project.SettingsFor(sequence);
        EffectDescriptor type = TransitionHelp.Type(command.Type, track, settings);
        Flicks duration = TransitionHelp.Duration(command.Duration, settings);

        List<(string Left, string Right)> cuts = [.. TransitionHelp.Cuts(track)
            .Where(pair => TransitionTiming.Between(track, pair.Left.Id, pair.Right.Id) is null)
            .Select(pair => (pair.Left.Id, pair.Right.Id))];

        // Refusing names every short cut before anything changes.
        if (command.Handles == TransitionHandles.Refuse)
        {
            var problems = new List<string>();
            foreach ((string leftId, string rightId) in cuts)
            {
                try
                {
                    TransitionHelp.Add(project, track.Id, track.Clip(leftId)!, track.Clip(rightId)!, type, duration, command.Alignment, TransitionHandles.Refuse, Id.New(), new HandlerContext());
                }
                catch (CommandException refused) when (refused.Code == "insufficient-handles")
                {
                    problems.Add(refused.Message);
                }
            }

            if (problems.Count > 0)
            {
                throw new CommandException(
                    "insufficient-handles",
                    $"{problems.Count} of the {cuts.Count} cuts are short of source, so none were added. {string.Join(" ", problems)}");
            }
        }

        foreach ((string leftId, string rightId) in cuts)
        {
            (_, Track current) = HandlerHelp.Track(project, track.Id);
            if (current.Clip(leftId) is not { } left || current.Clip(rightId) is not { } right || left.End != right.Start)
            {
                continue;
            }

            string id = Id.New();
            (project, _, _) = TransitionHelp.Add(project, track.Id, left, right, type, duration, command.Alignment, command.Handles, id, context);

            if (command.Audio && track.Kind == TrackKind.Video)
            {
                (_, Track now, Transition added) = TransitionHelp.Find(project, id);
                project = TransitionHelp.AddLinkedAudio(project, now.Id, added, context);
            }
        }

        return project;
    }
}

/// <summary>Sets what the default transition shortcuts add.</summary>
public sealed class SetDefaultTransitionHandler : ICommandHandler<SetDefaultTransitionCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetDefaultTransitionCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        TransitionDefaults defaults = project.Settings.EffectiveTransitions;

        if (command.Type is { Length: > 0 } typeId)
        {
            EffectDescriptor type = EffectCatalog.Registry.Require(typeId);
            defaults = type.Kind switch
            {
                EffectKind.Transition => defaults with { Video = type.TypeId },
                EffectKind.AudioTransition => defaults with { Audio = type.TypeId },
                _ => throw new CommandException("not-a-transition", $"'{typeId}' is not a transition, so it cannot be the default one."),
            };
        }

        if (command.Duration is { } duration)
        {
            defaults = defaults with { Duration = TransitionHelp.Duration(duration, project.Settings) };
        }

        TransitionDefaults? stored = defaults == TransitionDefaults.Standard ? null : defaults;
        if (stored == project.Settings.Transitions)
        {
            return project;
        }

        context.Changed(project.Id);
        return project with { Settings = project.Settings with { Transitions = stored } };
    }
}

/// <summary>Lists transitions and where they play.</summary>
public sealed class ListTransitionsHandler : IQueryHandler<ListTransitionsQuery, TransitionInfo[]>
{
    /// <inheritdoc />
    public TransitionInfo[] Handle(Project project, ListTransitionsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(query);

        if (query.TrackId is { Length: > 0 } trackId)
        {
            (Sequence on, Track track) = HandlerHelp.Track(project, trackId);
            return [.. track.Transitions.Select(transition => TransitionHelp.Info(project, on, track, transition))];
        }

        Sequence sequence = HandlerHelp.Sequence(project, query.SequenceId);
        return [.. sequence.Tracks.SelectMany(track => track.Transitions.Select(transition => TransitionHelp.Info(project, sequence, track, transition)))];
    }
}
