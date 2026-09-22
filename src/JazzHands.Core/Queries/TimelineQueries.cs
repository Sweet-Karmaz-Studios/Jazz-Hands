using System.Collections.Immutable;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Queries;

/// <summary>An empty stretch on a track between two clips, or before the first one.</summary>
/// <param name="Range">Where the gap is.</param>
/// <param name="BeforeClipId">The clip after the gap, or null when the gap runs to the end.</param>
/// <param name="AfterClipId">The clip before the gap, or null when the gap starts at zero.</param>
public sealed record Gap(TimeRange Range, string? BeforeClipId, string? AfterClipId);

/// <summary>
/// Read-only questions about a timeline: what is playing, where the edits are, what moves
/// together.
/// </summary>
/// <remarks>
/// The compositor, the playhead, snapping, and <c>jazz describe</c> all ask these, so they are
/// written once and kept allocation-light. Nothing here mutates anything.
/// </remarks>
public static class TimelineQueries
{
    /// <summary>The clip playing on a track at a time, or null in a gap.</summary>
    public static Clip? ClipAt(Track track, Flicks time)
    {
        ArgumentNullException.ThrowIfNull(track);

        // Clips are sorted by start, so a binary search finds the candidate in log time.
        EquatableArray<Clip> clips = track.Clips;
        int low = 0;
        int high = clips.Length - 1;

        while (low <= high)
        {
            int middle = (low + high) / 2;
            Clip candidate = clips[middle];

            if (candidate.Range.Contains(time))
            {
                return candidate;
            }

            if (time < candidate.Start)
            {
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }

        return null;
    }

    /// <summary>
    /// Every clip playing at a time across a sequence, bottom track first, which is the order the
    /// compositor stacks them in.
    /// </summary>
    public static ImmutableArray<Clip> ClipsAt(Sequence sequence, Flicks time, TrackKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var found = ImmutableArray.CreateBuilder<Clip>();
        foreach (Track track in sequence.Tracks)
        {
            if (kind is { } wanted && track.Kind != wanted)
            {
                continue;
            }

            if (ClipAt(track, time) is { } clip)
            {
                found.Add(clip);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>The empty stretches on a track, in order.</summary>
    public static ImmutableArray<Gap> Gaps(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var gaps = ImmutableArray.CreateBuilder<Gap>();
        Flicks cursor = Flicks.Zero;
        string? previousId = null;

        foreach (Clip clip in track.Clips)
        {
            if (clip.Start > cursor)
            {
                gaps.Add(new Gap(TimeRange.FromBounds(cursor, clip.Start), clip.Id, previousId));
            }

            cursor = Flicks.Max(cursor, clip.End);
            previousId = clip.Id;
        }

        return gaps.ToImmutable();
    }

    /// <summary>
    /// Every point on a track where something starts or ends, in order and without duplicates.
    /// These are what the playhead jumps between and what dragging snaps to.
    /// </summary>
    public static ImmutableArray<Flicks> EditPoints(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var points = new SortedSet<long>();
        foreach (Clip clip in track.Clips)
        {
            points.Add(clip.Start.Value);
            points.Add(clip.End.Value);
        }

        var result = ImmutableArray.CreateBuilder<Flicks>(points.Count);
        foreach (long point in points)
        {
            result.Add(new Flicks(point));
        }

        return result.ToImmutable();
    }

    /// <summary>Every edit point across a sequence, merged and sorted.</summary>
    public static ImmutableArray<Flicks> EditPoints(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var points = new SortedSet<long>();
        foreach (Track track in sequence.Tracks)
        {
            foreach (Clip clip in track.Clips)
            {
                points.Add(clip.Start.Value);
                points.Add(clip.End.Value);
            }
        }

        var result = ImmutableArray.CreateBuilder<Flicks>(points.Count);
        foreach (long point in points)
        {
            result.Add(new Flicks(point));
        }

        return result.ToImmutable();
    }

    /// <summary>The next edit point strictly after a time, or null when there is none.</summary>
    public static Flicks? NextEditPoint(Sequence sequence, Flicks after)
    {
        foreach (Flicks point in EditPoints(sequence))
        {
            if (point > after)
            {
                return point;
            }
        }

        return null;
    }

    /// <summary>The previous edit point strictly before a time, or null when there is none.</summary>
    public static Flicks? PreviousEditPoint(Sequence sequence, Flicks before)
    {
        Flicks? best = null;
        foreach (Flicks point in EditPoints(sequence))
        {
            if (point >= before)
            {
                break;
            }

            best = point;
        }

        return best;
    }

    /// <summary>
    /// Every clip linked to a clip, including itself. Video and its audio are inserted linked, so
    /// moving one moves the other unless the link is broken.
    /// </summary>
    public static ImmutableArray<Clip> LinkedClips(Sequence sequence, string clipId)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        Clip? target = null;
        foreach (Track track in sequence.Tracks)
        {
            target = track.Clip(clipId);
            if (target is not null)
            {
                break;
            }
        }

        if (target is null)
        {
            return [];
        }

        if (target.LinkGroupId is not { } group)
        {
            return [target];
        }

        var linked = ImmutableArray.CreateBuilder<Clip>();
        foreach (Track track in sequence.Tracks)
        {
            foreach (Clip clip in track.Clips)
            {
                if (string.Equals(clip.LinkGroupId, group, StringComparison.Ordinal))
                {
                    linked.Add(clip);
                }
            }
        }

        return linked.ToImmutable();
    }

    /// <summary>
    /// How much source material a clip consumes, which is its timeline duration scaled by speed.
    /// A clip at half speed uses half the source it occupies.
    /// </summary>
    public static Flicks EffectiveSourceDuration(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.SourceDuration;
    }

    /// <summary>
    /// The frame of a sequence shown at a time, on the sequence's own frame grid. Floor, because
    /// the frame on screen at t is the one that started at or before t.
    /// </summary>
    public static long FrameAt(ProjectSettings settings, Flicks time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return time.ToFrames(settings.FrameRate, RoundingMode.Floor);
    }

    /// <summary>True when any track in the sequence is soloed, which mutes the rest.</summary>
    public static bool HasSolo(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        foreach (Track track in sequence.Tracks)
        {
            if (track.Solo)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a track contributes to the output, taking mute and the solo rule into account.
    /// </summary>
    public static bool IsAudible(Sequence sequence, Track track)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);

        if (track.Muted)
        {
            return false;
        }

        return !HasSolo(sequence) || track.Solo;
    }
}
