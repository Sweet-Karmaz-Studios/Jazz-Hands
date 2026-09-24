using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Editing;

/// <summary>
/// The editing operations, as pure functions over a track or a sequence.
/// </summary>
/// <remarks>
/// Both the command handlers and the timeline's direct manipulation tools call these, which is
/// why they live here rather than in either. Dragging a trim handle and typing
/// <c>jazz clip trim</c> must produce the same project, or the two surfaces drift apart and one
/// of them becomes the real editor.
///
/// Every one takes a track or a sequence and returns a new one. Nothing here consults the clock,
/// the file system or the media; a trim that runs off the end of the source is caught by the
/// caller passing the source duration in.
/// </remarks>
public static partial class EditOps
{
    /// <summary>
    /// Splits a clip in two at a timeline position. The left piece keeps the identifier, so
    /// anything referring to the clip still refers to the first half.
    /// </summary>
    /// <param name="track">The track holding the clip.</param>
    /// <param name="clipId">The clip to split.</param>
    /// <param name="at">Where to cut, on the timeline.</param>
    /// <param name="newClipId">The identifier for the right piece, or null to generate one.</param>
    public static EditResult<Track> Split(Track track, string clipId, Flicks at, string? newClipId = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        if (track.Clip(clipId) is not { } clip)
        {
            return EditError.ClipNotFound(clipId);
        }

        if (at <= clip.Start || at >= clip.End)
        {
            return EditError.TimeOutOfRange(
                $"Cannot split '{clip.Name}' at {Timecode.FormatClock(at)}: it runs from "
                + $"{Timecode.FormatClock(clip.Start)} to {Timecode.FormatClock(clip.End)}.");
        }

        Flicks leftDuration = at - clip.Start;
        Clip left = clip with { Range = new TimeRange(clip.Start, leftDuration) };

        Clip right = clip with
        {
            Id = newClipId ?? Id.New(),
            Range = TimeRange.FromBounds(at, clip.End),
            SourceIn = clip.SourceTimeAt(at),

            // A split inherits the fades at the outer ends only; the new inner edges are hard.
            FadeIn = Fade.None,
            Markers = MarkersAfter(clip, leftDuration),
        };

        left = left with
        {
            FadeOut = Fade.None,
            Markers = MarkersBefore(clip, leftDuration),
        };

        return TransitionOps.AfterSplit(track.ReplaceClip(left).AddClip(right), clip.Id, right.Id);
    }

    /// <summary>
    /// Moves a clip's start edge, keeping the source material under it fixed. Trimming in makes
    /// the clip shorter from the front; trimming out extends it, up to the source available.
    /// </summary>
    /// <param name="track">The track holding the clip.</param>
    /// <param name="clipId">The clip to trim.</param>
    /// <param name="to">The new start, on the timeline.</param>
    /// <param name="ripple">Move the following clips by the same amount to close or open the gap.</param>
    public static EditResult<Track> TrimIn(
        Track track,
        string clipId,
        Flicks to,
        bool ripple = false)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        if (track.Clip(clipId) is not { } clip)
        {
            return EditError.ClipNotFound(clipId);
        }

        if (to >= clip.End)
        {
            return EditError.EmptyResult(
                $"Trimming '{clip.Name}' to {Timecode.FormatClock(to)} would leave nothing of it.");
        }

        Flicks delta = to - clip.Start;
        Flicks newSourceIn = clip.IsHold ? clip.SourceIn : clip.SourceIn + Clip.ScaleBySpeed(delta, clip.EffectiveSpeed);
        if (newSourceIn.IsNegative)
        {
            return EditError.NoSourceLeft(
                $"'{clip.Name}' has no source material before its current start.");
        }

        if (ripple)
        {
            // A ripple trim keeps the clip where it starts and takes the material off its front;
            // what follows closes up behind it, or makes room when it grows.
            Clip rippled = clip with { Range = TimeRange.FromBounds(clip.Start, clip.End - delta), SourceIn = newSourceIn };
            return ShiftFrom(track.ReplaceClip(rippled), clip.End, -delta, clip.Id);
        }

        Clip trimmed = clip with
        {
            Range = TimeRange.FromBounds(to, clip.End),
            SourceIn = newSourceIn,
        };

        if (delta.IsNegative && OverlapsPrevious(track, trimmed))
        {
            return EditError.WouldOverlap($"Extending '{clip.Name}' backwards would overlap the clip before it.");
        }

        return track.ReplaceClip(trimmed);
    }

    /// <summary>
    /// Moves a clip's end edge, keeping the source material under it fixed.
    /// </summary>
    /// <param name="track">The track holding the clip.</param>
    /// <param name="clipId">The clip to trim.</param>
    /// <param name="to">The new end, on the timeline, exclusive.</param>
    /// <param name="ripple">Move the following clips by the same amount.</param>
    /// <param name="sourceDuration">How much source the clip has, so it cannot run off the end.</param>
    public static EditResult<Track> TrimOut(
        Track track,
        string clipId,
        Flicks to,
        bool ripple = false,
        Flicks? sourceDuration = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        if (track.Clip(clipId) is not { } clip)
        {
            return EditError.ClipNotFound(clipId);
        }

        if (to <= clip.Start)
        {
            return EditError.EmptyResult(
                $"Trimming '{clip.Name}' to {Timecode.FormatClock(to)} would leave nothing of it.");
        }

        Clip trimmed = clip with { Range = TimeRange.FromBounds(clip.Start, to) };

        if (!clip.IsHold && sourceDuration is { } available && clip.SourceIn + trimmed.SourceDuration > available)
        {
            return EditError.NoSourceLeft(
                $"'{clip.Name}' only has {Timecode.FormatClock(available - clip.SourceIn)} of source left.");
        }

        Flicks delta = to - clip.End;
        if (!ripple && !delta.IsNegative && OverlapsNext(track, trimmed))
        {
            return EditError.WouldOverlap($"Extending '{clip.Name}' would overlap the clip after it.");
        }

        Track updated = track.ReplaceClip(trimmed);
        return ripple ? ShiftFrom(updated, clip.End, delta, trimmed.Id) : updated;
    }

    /// <summary>
    /// Moves the cut between two touching clips, trimming one and extending the other. The
    /// clips keep their positions on the timeline; only the edit point moves.
    /// </summary>
    /// <param name="track">The track holding both clips.</param>
    /// <param name="leftClipId">The clip before the cut.</param>
    /// <param name="rightClipId">The clip after the cut.</param>
    /// <param name="delta">How far to move the cut. Positive is later.</param>
    /// <param name="leftSourceDuration">How much source the left clip has.</param>
    public static EditResult<Track> Roll(
        Track track,
        string leftClipId,
        string rightClipId,
        Flicks delta,
        Flicks? leftSourceDuration = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        if (track.Clip(leftClipId) is not { } left)
        {
            return EditError.ClipNotFound(leftClipId);
        }

        if (track.Clip(rightClipId) is not { } right)
        {
            return EditError.ClipNotFound(rightClipId);
        }

        if (left.End != right.Start)
        {
            return EditError.NotAdjacent(
                $"'{left.Name}' ends at {Timecode.FormatClock(left.End)} and '{right.Name}' starts at "
                + $"{Timecode.FormatClock(right.Start)}; a roll needs them touching.");
        }

        Flicks cut = left.End + delta;
        if (cut <= left.Start)
        {
            return EditError.EmptyResult($"Rolling that far would leave nothing of '{left.Name}'.");
        }

        if (cut >= right.End)
        {
            return EditError.EmptyResult($"Rolling that far would leave nothing of '{right.Name}'.");
        }

        Clip newLeft = left with { Range = TimeRange.FromBounds(left.Start, cut) };
        if (!left.IsHold && leftSourceDuration is { } available && left.SourceIn + newLeft.SourceDuration > available)
        {
            return EditError.NoSourceLeft($"'{left.Name}' has no source material past its current end.");
        }

        Flicks newRightSourceIn = right.IsHold ? right.SourceIn : right.SourceIn + Clip.ScaleBySpeed(delta, right.EffectiveSpeed);
        if (newRightSourceIn.IsNegative)
        {
            return EditError.NoSourceLeft($"'{right.Name}' has no source material before its current start.");
        }

        Clip newRight = right with
        {
            Range = TimeRange.FromBounds(cut, right.End),
            SourceIn = newRightSourceIn,
        };

        return track.ReplaceClip(newLeft).ReplaceClip(newRight);
    }

    /// <summary>
    /// Moves the source material under a clip without moving the clip. The clip stays exactly
    /// where it is and shows a different part of the shot.
    /// </summary>
    /// <param name="track">The track holding the clip.</param>
    /// <param name="clipId">The clip to slip.</param>
    /// <param name="delta">How far to slip, in source time. Positive shows later material.</param>
    /// <param name="sourceDuration">How much source the clip has.</param>
    public static EditResult<Track> Slip(Track track, string clipId, Flicks delta, Flicks? sourceDuration = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        if (track.Clip(clipId) is not { } clip)
        {
            return EditError.ClipNotFound(clipId);
        }

        Flicks newSourceIn = clip.SourceIn + delta;
        if (newSourceIn.IsNegative)
        {
            return EditError.NoSourceLeft($"Slipping '{clip.Name}' that far would run off the front of the source.");
        }

        if (sourceDuration is { } available && newSourceIn + clip.SourceDuration > available)
        {
            return EditError.NoSourceLeft($"Slipping '{clip.Name}' that far would run off the end of the source.");
        }

        return track.ReplaceClip(clip with { SourceIn = newSourceIn });
    }

    /// <summary>
    /// Moves a clip along the timeline, trimming its neighbours to make room. The clip keeps its
    /// duration and its source; the clips either side give and take.
    /// </summary>
    /// <param name="track">The track holding the clip.</param>
    /// <param name="clipId">The clip to slide.</param>
    /// <param name="delta">How far to slide. Positive is later.</param>
    public static EditResult<Track> Slide(Track track, string clipId, Flicks delta)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        int index = track.IndexOf(clipId);
        if (index < 0)
        {
            return EditError.ClipNotFound(clipId);
        }

        Clip clip = track.Clips[index];
        Clip? previous = index > 0 ? track.Clips[index - 1] : null;
        Clip? next = index < track.Clips.Length - 1 ? track.Clips[index + 1] : null;

        Clip moved = clip with { Range = clip.Range.Shift(delta) };

        if (previous is not null && previous.End != clip.Start && delta.IsNegative)
        {
            // There is a gap before the clip; sliding into it does not need the neighbour trimmed.
            if (moved.Start < previous.End)
            {
                return EditError.WouldOverlap($"Sliding '{clip.Name}' that far would overlap '{previous.Name}'.");
            }
        }

        Track updated = track;

        if (previous is not null && previous.End == clip.Start)
        {
            if (moved.Start <= previous.Start)
            {
                return EditError.EmptyResult($"Sliding that far would leave nothing of '{previous.Name}'.");
            }

            updated = updated.ReplaceClip(previous with { Range = TimeRange.FromBounds(previous.Start, moved.Start) });
        }

        if (next is not null && next.Start == clip.End)
        {
            if (moved.End >= next.End)
            {
                return EditError.EmptyResult($"Sliding that far would leave nothing of '{next.Name}'.");
            }

            Flicks nextSourceIn = next.IsHold ? next.SourceIn : next.SourceIn + Clip.ScaleBySpeed(moved.End - next.Start, next.EffectiveSpeed);
            if (nextSourceIn.IsNegative)
            {
                return EditError.NoSourceLeft($"'{next.Name}' has no source material before its current start.");
            }

            updated = updated.ReplaceClip(next with
            {
                Range = TimeRange.FromBounds(moved.End, next.End),
                SourceIn = nextSourceIn,
            });
        }
        else if (next is not null && moved.End > next.Start)
        {
            return EditError.WouldOverlap($"Sliding '{clip.Name}' that far would overlap '{next.Name}'.");
        }

        return updated.ReplaceClip(moved);
    }

    /// <summary>
    /// Removes a clip and closes the gap, pulling everything after it earlier.
    /// </summary>
    public static EditResult<Track> RippleDelete(Track track, string clipId)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        if (track.Clip(clipId) is not { } clip)
        {
            return EditError.ClipNotFound(clipId);
        }

        Track removed = track.RemoveClip(clipId);
        return ShiftFrom(removed, clip.End, -clip.Duration, clipId);
    }

    /// <summary>
    /// Removes a clip and leaves the gap, which is what the delete key does.
    /// </summary>
    public static EditResult<Track> Lift(Track track, string clipId)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        return track.Clip(clipId) is null
            ? EditError.ClipNotFound(clipId)
            : track.RemoveClip(clipId);
    }

    /// <summary>
    /// Moves a clip to a position, optionally onto another track.
    /// </summary>
    /// <param name="sequence">The sequence holding both tracks.</param>
    /// <param name="clipId">The clip to move.</param>
    /// <param name="toTrackId">The destination track, or null to stay put.</param>
    /// <param name="toStart">The new start on the timeline.</param>
    /// <param name="allowOverlap">
    /// Let the clip land on top of another. The timeline allows this while dragging and resolves
    /// it on drop; commands do not.
    /// </param>
    public static EditResult<Sequence> Move(
        Sequence sequence,
        string clipId,
        string? toTrackId,
        Flicks toStart,
        bool allowOverlap = false)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        Track? sourceTrack = sequence.TrackOf(clipId);
        if (sourceTrack is null)
        {
            return EditError.ClipNotFound(clipId);
        }

        if (sourceTrack.Locked)
        {
            return EditError.TrackLocked(sourceTrack.Name);
        }

        Track destination = toTrackId is null ? sourceTrack : sequence.Track(toTrackId)!;
        if (toTrackId is not null && sequence.Track(toTrackId) is null)
        {
            return EditError.TrackNotFound(toTrackId);
        }

        if (destination.Locked)
        {
            return EditError.TrackLocked(destination.Name);
        }

        if (toStart.IsNegative)
        {
            return EditError.TimeOutOfRange("A clip cannot start before the beginning of the sequence.");
        }

        Clip clip = sourceTrack.Clip(clipId)!;
        Clip moved = clip with { Range = clip.Range.WithStart(toStart) };

        bool sameTrack = string.Equals(sourceTrack.Id, destination.Id, StringComparison.Ordinal);
        Track withoutClip = sameTrack ? sourceTrack.RemoveClip(clipId) : destination;

        if (!allowOverlap && Overlaps(withoutClip, moved))
        {
            return EditError.WouldOverlap(
                $"'{clip.Name}' would overlap another clip at {Timecode.FormatClock(toStart)}.");
        }

        if (sameTrack)
        {
            return sequence.ReplaceTrack(withoutClip.AddClip(moved));
        }

        return sequence
            .ReplaceTrack(sourceTrack.RemoveClip(clipId))
            .ReplaceTrack(destination.AddClip(moved));
    }

    /// <summary>
    /// Changes a clip's duration by changing its speed, keeping all of its source material.
    /// This is the rate stretch tool: the clip plays faster or slower rather than being trimmed.
    /// </summary>
    /// <param name="track">The track holding the clip.</param>
    /// <param name="clipId">The clip to stretch.</param>
    /// <param name="newDuration">The duration it should occupy on the timeline.</param>
    public static EditResult<Track> RateStretch(Track track, string clipId, Flicks newDuration)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        if (track.Clip(clipId) is not { } clip)
        {
            return EditError.ClipNotFound(clipId);
        }

        if (newDuration.Value <= 0)
        {
            return EditError.EmptyResult($"'{clip.Name}' cannot be stretched to nothing.");
        }

        // Keep the same source material: new speed is the source duration over the new timeline
        // duration, exactly, so a clip stretched and stretched back lands on its original rate.
        Flicks sourceDuration = clip.SourceDuration;
        var newSpeed = new Rational(sourceDuration.Value, newDuration.Value);

        if (newSpeed.IsZero)
        {
            return EditError.EmptyResult($"'{clip.Name}' has no source material to stretch.");
        }

        Clip stretched = clip with
        {
            Range = clip.Range.WithDuration(newDuration),
            Speed = newSpeed,
        };

        return track.ReplaceClip(stretched);
    }

    /// <summary>
    /// Replaces a run of clips with a single clip playing a new sequence that contains them.
    /// </summary>
    /// <remarks>
    /// The nested clips keep their positions relative to the earliest one, so the compound plays
    /// back exactly as the run did. Returns the new sequence and the replacement clip alongside
    /// the edited one, because the caller has to add the sequence to the project.
    /// </remarks>
    /// <param name="sequence">The sequence holding the clips.</param>
    /// <param name="clipIds">The clips to nest. They must all be on the same track.</param>
    /// <param name="name">The name for the new sequence.</param>
    /// <param name="settings">Settings for the new sequence, or null to inherit.</param>
    public static EditResult<NestResult> NestSelection(
        Sequence sequence,
        IReadOnlyList<string> clipIds,
        string name,
        ProjectSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clipIds);

        if (clipIds.Count == 0)
        {
            return EditError.NothingSelected("Nesting needs at least one clip.");
        }

        Track? track = sequence.TrackOf(clipIds[0]);
        if (track is null)
        {
            return EditError.ClipNotFound(clipIds[0]);
        }

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        var selected = new List<Clip>(clipIds.Count);
        foreach (string clipId in clipIds)
        {
            if (track.Clip(clipId) is not { } clip)
            {
                return sequence.TrackOf(clipId) is null
                    ? EditError.ClipNotFound(clipId)
                    : EditError.NotAdjacent("Nesting needs every clip to be on the same track.");
            }

            selected.Add(clip);
        }

        selected.Sort((left, right) => left.Range.CompareTo(right.Range));
        Flicks start = selected[0].Start;
        Flicks end = selected[^1].End;

        var innerTrack = new Track(
            Id.New(),
            track.Kind,
            track.Name,
            0,
            new EquatableArray<Clip>(selected.Select(clip => clip with { Range = clip.Range.Shift(-start) })));

        var nested = new Sequence(Id.New(), name, EquatableArray.Create(innerTrack), Settings: settings);

        var compound = new Clip(
            Id.New(),
            TimeRange.FromBounds(start, end),
            Flicks.Zero,
            SequenceId: nested.Id,
            Name: name);

        Track edited = track;
        foreach (Clip clip in selected)
        {
            edited = edited.RemoveClip(clip.Id);
        }

        edited = edited.AddClip(compound);

        return new NestResult(sequence.ReplaceTrack(edited), nested, compound);
    }

    /// <summary>Whether a clip would overlap anything already on a track.</summary>
    public static bool Overlaps(Track track, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);

        foreach (Clip existing in track.Clips)
        {
            if (!string.Equals(existing.Id, clip.Id, StringComparison.Ordinal) &&
                existing.Range.Intersects(clip.Range))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Moves every clip starting at or after a position by a delta, skipping one clip.</summary>
    /// <remarks>
    /// Callers pull clips back by no more than the edited clip lost, or push them on, so nothing
    /// moves before zero.
    /// </remarks>
    private static Track ShiftFrom(Track track, Flicks from, Flicks delta, string exceptClipId)
    {
        if (delta.IsZero)
        {
            return track;
        }

        Track updated = track;
        foreach (Clip clip in track.Clips)
        {
            if (string.Equals(clip.Id, exceptClipId, StringComparison.Ordinal) || clip.Start < from)
            {
                continue;
            }

            updated = updated.ReplaceClip(clip with { Range = clip.Range.Shift(delta) });
        }

        return updated;
    }

    private static bool OverlapsPrevious(Track track, Clip clip)
    {
        foreach (Clip existing in track.Clips)
        {
            if (!string.Equals(existing.Id, clip.Id, StringComparison.Ordinal) &&
                existing.Start < clip.Start &&
                existing.End > clip.Start)
            {
                return true;
            }
        }

        return false;
    }

    private static bool OverlapsNext(Track track, Clip clip)
    {
        foreach (Clip existing in track.Clips)
        {
            if (!string.Equals(existing.Id, clip.Id, StringComparison.Ordinal) &&
                existing.Start >= clip.Start &&
                existing.Start < clip.End)
            {
                return true;
            }
        }

        return false;
    }

    private static EquatableArray<Marker> MarkersBefore(Clip clip, Flicks cutOffset)
    {
        if (clip.Markers.IsEmpty)
        {
            return clip.Markers;
        }

        return new EquatableArray<Marker>(clip.Markers.Where(marker => marker.Time < cutOffset));
    }

    private static EquatableArray<Marker> MarkersAfter(Clip clip, Flicks cutOffset)
    {
        if (clip.Markers.IsEmpty)
        {
            return clip.Markers;
        }

        return new EquatableArray<Marker>(clip.Markers
            .Where(marker => marker.Time >= cutOffset)
            .Select(marker => marker with { Time = marker.Time - cutOffset }));
    }
}

/// <summary>What nesting produced: the edited sequence, the new one, and the clip that plays it.</summary>
/// <param name="Sequence">The sequence the clips were taken out of.</param>
/// <param name="Nested">The new sequence holding them.</param>
/// <param name="CompoundClip">The clip that replaced them.</param>
public sealed record NestResult(Sequence Sequence, Sequence Nested, Clip CompoundClip);
