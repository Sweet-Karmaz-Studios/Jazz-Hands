using System.Collections.Immutable;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Queries;

/// <summary>
/// Where a transition plays: the stretch of the timeline around its cut, and how much of each
/// clip it takes.
/// </summary>
/// <param name="Transition">The transition.</param>
/// <param name="Left">The outgoing clip.</param>
/// <param name="Right">The incoming clip.</param>
/// <param name="Range">Where it plays on the timeline, after it has been fitted to the clips.</param>
public readonly record struct TransitionSpan(Transition Transition, Clip Left, Clip Right, TimeRange Range)
{
    /// <summary>The cut: where the outgoing clip ends and the incoming one starts.</summary>
    public Flicks Cut => Left.End;

    /// <summary>How much of the span is before the cut, over the end of the outgoing clip.</summary>
    public Flicks Before => Cut - Range.Start;

    /// <summary>How much is after the cut, over the start of the incoming clip.</summary>
    public Flicks After => Range.End - Cut;

    /// <summary>True when the span is shorter than the transition asks, because a clip is too short for it.</summary>
    public bool IsClamped => Range.Duration < Transition.Duration;

    /// <summary>How far through the span a time is, 0 at its start and 1 at its end, before easing.</summary>
    public float Progress(Flicks time) => Range.Duration.Value <= 0
        ? 1.0f
        : Math.Clamp((float)((double)(time - Range.Start).Value / Range.Duration.Value), 0.0f, 1.0f);
}

/// <summary>What a track shows or plays at one moment: one clip, or two across a transition.</summary>
/// <param name="Clip">The clip under the time, or the outgoing clip inside a transition.</param>
/// <param name="Incoming">The incoming clip inside a transition; null otherwise.</param>
/// <param name="Span">The transition playing, or null.</param>
public readonly record struct TrackMoment(Clip? Clip, Clip? Incoming, TransitionSpan? Span)
{
    /// <summary>Nothing plays.</summary>
    public static TrackMoment None => default;

    /// <summary>True inside a transition.</summary>
    public bool IsTransition => Span is not null;
}

/// <summary>
/// The timing of transitions: where each one plays, how much source it needs beyond each clip,
/// and what is on a track at a moment.
/// </summary>
/// <remarks>
/// A transition sits on a cut between two adjacent clips. Centred, half is before the cut and
/// half after; at the end of the outgoing clip, all before; at the start of the incoming one, all
/// after. A centred transition of an odd number of frames puts the odd frame after the cut, so
/// both halves are whole frames. Inside the span the outgoing clip plays on past its end and the
/// incoming one starts before its start, which is the source beyond the cut that editors call
/// handles. Where the file has less than that, the missing frames hold the last one there is
/// (<see cref="ClampToSource"/>), and validation says so.
///
/// A span never takes more of a clip than the clip has: a transition longer than its clips, or
/// one that would run into the transition at the clip's other end, is fitted, the earlier
/// transition keeping what it asked for. The file keeps the duration that was asked for.
/// </remarks>
public static class TransitionTiming
{
    /// <summary>How a transition's duration splits around its cut, before it is fitted to the clips.</summary>
    public static (Flicks Before, Flicks After) Split(Flicks duration, TransitionAlignment alignment, Rational frameRate)
    {
        if (duration.Value <= 0)
        {
            return (Flicks.Zero, Flicks.Zero);
        }

        switch (alignment)
        {
            case TransitionAlignment.EndOfLeft:
                return (duration, Flicks.Zero);

            case TransitionAlignment.StartOfRight:
                return (Flicks.Zero, duration);
        }

        // Whole frames on both sides when the duration is whole frames: the odd one goes after.
        long frames = duration.ToFrames(frameRate, RoundingMode.Floor);
        Flicks before = !frameRate.IsZero && Flicks.FromFrames(frames, frameRate) == duration
            ? Flicks.FromFrames(frames / 2, frameRate)
            : duration / 2;

        return (before, duration - before);
    }

    /// <summary>Where a transition plays on its track, or null when its clips are not there or not adjacent.</summary>
    public static TransitionSpan? Span(Track track, Transition transition, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(transition);

        if (track.Clip(transition.LeftClipId) is not { } left
            || track.Clip(transition.RightClipId) is not { } right
            || left.End != right.Start)
        {
            return null;
        }

        (Flicks before, Flicks after) = Split(transition.Duration, transition.Alignment, frameRate);

        // Each side stays inside its clip. The earlier of two transitions on one clip keeps what it
        // asked for, so the outgoing side also leaves what the transition at that clip's start takes.
        Flicks leftRoom = left.Duration - TakenAtStart(track, left, frameRate);
        Flicks rightRoom = right.Duration;
        before = Flicks.Clamp(before, Flicks.Zero, Flicks.Max(leftRoom, Flicks.Zero));
        after = Flicks.Clamp(after, Flicks.Zero, Flicks.Max(rightRoom, Flicks.Zero));

        if ((before + after).Value <= 0)
        {
            return null;
        }

        return new TransitionSpan(transition, left, right, TimeRange.FromBounds(left.End - before, right.Start + after));
    }

    /// <summary>Every transition on a track that plays, in order along the track.</summary>
    public static ImmutableArray<TransitionSpan> Spans(Track track, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Transitions.IsEmpty)
        {
            return [];
        }

        var spans = ImmutableArray.CreateBuilder<TransitionSpan>(track.Transitions.Length);
        foreach (Transition transition in track.Transitions)
        {
            if (Span(track, transition, frameRate) is { } span)
            {
                spans.Add(span);
            }
        }

        spans.Sort((a, b) => a.Range.Start.CompareTo(b.Range.Start));
        return spans.ToImmutable();
    }

    /// <summary>What a track plays at a time: a clip, two clips across a transition, or nothing.</summary>
    public static TrackMoment At(Track track, Flicks time, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (TimelineQueries.ClipAt(track, time) is not { } clip)
        {
            return TrackMoment.None;
        }

        // A span lies over the ends of its two clips, so one playing now touches the clip under
        // the time: only those need fitting.
        foreach (Transition transition in track.Transitions)
        {
            if (transition.Touches(clip.Id) && Span(track, transition, frameRate) is { } span && span.Range.Contains(time))
            {
                return new TrackMoment(span.Left, span.Right, span);
            }
        }

        return new TrackMoment(clip, null, null);
    }

    /// <summary>The transition on a track that joins two clips, or null.</summary>
    public static Transition? Between(Track track, string leftClipId, string rightClipId)
    {
        ArgumentNullException.ThrowIfNull(track);

        foreach (Transition transition in track.Transitions)
        {
            if (string.Equals(transition.LeftClipId, leftClipId, StringComparison.Ordinal)
                && string.Equals(transition.RightClipId, rightClipId, StringComparison.Ordinal))
            {
                return transition;
            }
        }

        return null;
    }

    /// <summary>
    /// How much timeline the source of a clip has after the clip's end: the most a transition can
    /// run past it. <see cref="Flicks.MaxValue"/> for a source without an end (a still, a
    /// generator, a compound clip, a freeze frame).
    /// </summary>
    public static Flicks HandleAfter(Project project, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);

        if (SourceLength(project, clip) is not { } length)
        {
            return Flicks.MaxValue;
        }

        // Played backwards, the timeline after the clip is the source before its in point.
        Flicks spare = clip.Reverse ? clip.SourceIn : length - clip.SourceOut;
        return ToTimeline(spare, clip);
    }

    /// <summary>How much timeline the source has before the clip's start: the most a transition can start ahead of it.</summary>
    public static Flicks HandleBefore(Project project, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);

        if (SourceLength(project, clip) is not { } length)
        {
            return Flicks.MaxValue;
        }

        Flicks spare = clip.Reverse ? length - clip.SourceOut : clip.SourceIn;
        return ToTimeline(spare, clip);
    }

    /// <summary>
    /// How much each clip of a span is short of the source the span needs past the cut: the
    /// outgoing clip after its end and the incoming one before its start. Zero when there is enough.
    /// </summary>
    public static (Flicks Left, Flicks Right) Shortfall(Project project, TransitionSpan span)
    {
        ArgumentNullException.ThrowIfNull(project);

        Flicks left = Short(span.After, HandleAfter(project, span.Left));
        Flicks right = Short(span.Before, HandleBefore(project, span.Right));
        return (left, right);
    }

    /// <summary>
    /// A timeline time for a clip, moved inside the source it has: a transition that needs more
    /// than the file holds shows the file's last frame, or its first, for the rest.
    /// </summary>
    public static Flicks ClampToSource(Project project, Clip clip, Flicks time)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);

        if (time >= clip.End)
        {
            Flicks after = HandleAfter(project, clip);
            if (after != Flicks.MaxValue && time >= clip.End + after)
            {
                // One flick inside, so the frame found is the last there is rather than one past it.
                return Flicks.Max(clip.Start, clip.End + after - Flicks.Epsilon);
            }
        }
        else if (time < clip.Start)
        {
            Flicks before = HandleBefore(project, clip);
            if (before != Flicks.MaxValue && time < clip.Start - before)
            {
                return clip.Start - before;
            }
        }

        return time;
    }

    /// <summary>How long the source of a clip is, or null when it has no end.</summary>
    private static Flicks? SourceLength(Project project, Clip clip)
    {
        if (clip.IsHold || clip.MediaId is not { } mediaId || project.MediaItem(mediaId) is not { } item || item.Kind == MediaKind.Still)
        {
            return null;
        }

        return item.Duration;
    }

    private static Flicks ToTimeline(Flicks source, Clip clip) =>
        source.Value <= 0 ? Flicks.Zero : Clip.ScaleBySpeed(source, clip.EffectiveSpeed.Inverse);

    private static Flicks Short(Flicks needed, Flicks available) =>
        available == Flicks.MaxValue || needed <= available ? Flicks.Zero : needed - available;

    /// <summary>How much of a clip the transition at its start takes, as it asked, inside the clip.</summary>
    private static Flicks TakenAtStart(Track track, Clip clip, Rational frameRate)
    {
        foreach (Transition other in track.Transitions)
        {
            if (string.Equals(other.RightClipId, clip.Id, StringComparison.Ordinal))
            {
                (Flicks _, Flicks after) = Split(other.Duration, other.Alignment, frameRate);
                return Flicks.Clamp(after, Flicks.Zero, clip.Duration);
            }
        }

        return Flicks.Zero;
    }
}
