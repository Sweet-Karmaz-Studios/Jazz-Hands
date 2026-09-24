using System.Collections.Immutable;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Editing;

/// <summary>A new clip to put on a track, for <see cref="EditOps.Insert"/> and <see cref="EditOps.Overwrite"/>.</summary>
/// <param name="TrackId">The track it goes on.</param>
/// <param name="Clip">The clip. Its start is ignored: every placement goes at the edit's time.</param>
public sealed record Placement(string TrackId, Clip Clip);

/// <summary>
/// The editing operations that work across a whole sequence: ripples that keep sync-locked tracks
/// in step, inserts, overwrites, range lifts and extracts, freeze frames and the magnetic pass.
/// </summary>
/// <remarks>
/// Everything that moves time goes through <see cref="Ripple"/>, which is the one place the rule
/// lives: from a time on, the edited tracks and every sync-locked track shift by the same amount;
/// a locked track never moves; opening space splits whatever straddles the time; closing space
/// needs the span to be empty and names the clip in the way when it is not. That is Premiere's
/// sync lock, and it is what keeps sound under picture when a picture edit ripples.
///
/// Ids are taken literally, as everywhere in the command layer: a picture clip's linked sound is
/// edited when its id is given too, which is what the timeline does when a click selects both.
/// </remarks>
public static partial class EditOps
{
    /// <summary>
    /// Shifts everything from a time by an amount on the edited tracks and every sync-locked one.
    /// </summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="at">Where time moves from.</param>
    /// <param name="delta">How far: positive opens space, negative closes the span just before <paramref name="at"/>.</param>
    /// <param name="edited">The tracks the edit was on, which ripple whether sync locked or not.</param>
    public static EditResult<Sequence> Ripple(Sequence sequence, Flicks at, Flicks delta, IReadOnlyCollection<string> edited)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(edited);

        if (delta.IsZero)
        {
            return sequence;
        }

        if ((at + delta).IsNegative)
        {
            return EditError.TimeOutOfRange(
                $"Closing {Timecode.FormatClock(-delta)} before {Timecode.FormatClock(at)} would run past the start.");
        }

        Sequence result = sequence;

        foreach (Track track in sequence.Tracks)
        {
            bool isEdited = edited.Contains(track.Id);

            if (!isEdited && (!track.IsSyncLocked || track.Locked))
            {
                continue;
            }

            if (track.Locked)
            {
                return EditError.TrackLocked(track.Name);
            }

            EditResult<Track> shifted = delta.IsNegative
                ? Close(track, at, delta, isEdited)
                : Open(track, at, delta);

            if (!shifted.IsOk)
            {
                return shifted.Error!;
            }

            result = result.ReplaceTrack(shifted.Value);
        }

        return result;
    }

    /// <summary>
    /// Trims one edge of some clips, all at the same place, and ripples: the rest of the timeline
    /// follows the edge, so nothing is left as a gap and nothing is covered.
    /// </summary>
    /// <remarks>
    /// Trimming a start keeps the clip where it begins and takes material off its front; trimming
    /// an end takes it off the back. Either way the clips after it, on these tracks and every
    /// sync-locked one, move up or back by what the clip lost or gained.
    /// </remarks>
    /// <param name="sequence">The sequence.</param>
    /// <param name="clipIds">The clips, which must share the edge: a picture and its sound.</param>
    /// <param name="edge">Which edge.</param>
    /// <param name="to">Where the edge is dragged to, on the timeline.</param>
    /// <param name="sourceDuration">How much source a clip has, or null when it does not say.</param>
    public static EditResult<Sequence> RippleTrim(
        Sequence sequence,
        IReadOnlyList<string> clipIds,
        ClipEdge edge,
        Flicks to,
        Func<Clip, Flicks?>? sourceDuration = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clipIds);

        if (clipIds.Count == 0)
        {
            return EditError.NothingSelected("A ripple trim needs a clip.");
        }

        EditResult<List<(Track Track, Clip Clip)>> found = Find(sequence, clipIds);
        if (!found.IsOk)
        {
            return found.Error!;
        }

        List<(Track Track, Clip Clip)> clips = found.Value;
        Clip first = clips[0].Clip;
        Flicks edgeTime = edge == ClipEdge.Start ? first.Start : first.End;

        foreach ((Track _, Clip clip) in clips)
        {
            // Everything ripples from where the clips end, so for a start edge they have to end
            // together as well as start together.
            bool aligned = edge == ClipEdge.Start ? clip.Range == first.Range : clip.End == first.End;
            if (!aligned)
            {
                return EditError.NotAligned(
                    $"'{clip.Name}' does not {(edge == ClipEdge.Start ? "start" : "end")} where '{first.Name}' does, so they cannot be ripple trimmed together.");
            }
        }

        // What the edge move does to where each clip ends: the same for all of them.
        Flicks change = edge == ClipEdge.Start ? edgeTime - to : to - edgeTime;
        if (change.IsZero)
        {
            return sequence;
        }

        var trimmed = new List<(Track Track, Clip Clip)>(clips.Count);
        foreach ((Track track, Clip clip) in clips)
        {
            Flicks duration = clip.Duration + change;
            if (duration.Value <= 0)
            {
                return EditError.EmptyResult($"Ripple trimming '{clip.Name}' that far would leave nothing of it.");
            }

            Flicks sourceIn = clip.SourceIn;
            if (edge == ClipEdge.Start && !clip.IsHold)
            {
                sourceIn = clip.SourceIn - Clip.ScaleBySpeed(change, clip.EffectiveSpeed);
                if (sourceIn.IsNegative)
                {
                    return EditError.NoSourceLeft($"'{clip.Name}' has no source material before its current start.");
                }
            }

            Clip changed = clip with { Range = new TimeRange(clip.Start, duration), SourceIn = sourceIn };

            if (edge == ClipEdge.End && !clip.IsHold && sourceDuration?.Invoke(clip) is { } available
                && changed.SourceOut > available)
            {
                return EditError.NoSourceLeft(
                    $"'{clip.Name}' only has {Timecode.FormatClock(available - clip.SourceIn)} of source.");
            }

            trimmed.Add((track, changed));
        }

        string[] tracks = [.. clips.Select(entry => entry.Track.Id).Distinct(StringComparer.Ordinal)];

        // Make room first when growing, close up after when shrinking, so no clip ever overlaps.
        Sequence result = sequence;
        if (change.Value > 0)
        {
            // Opening space only fails on a locked edited track, and those were refused above.
            result = Ripple(result, first.End, change, tracks).Value;
        }

        foreach ((Track track, Clip clip) in trimmed)
        {
            result = result.ReplaceTrack(result.Track(track.Id)!.ReplaceClip(clip));
        }

        return change.IsNegative ? Ripple(result, first.End, change, tracks) : result;
    }

    /// <summary>
    /// Removes clips and closes the time they took, on their tracks and every sync-locked one.
    /// </summary>
    /// <remarks>
    /// The clips' ranges are joined into spans, and each span closes from the latest back. A span
    /// can only close where nothing else is in it: another clip on the same track, or anything on
    /// a sync-locked track, stops it with a message naming what is in the way.
    /// </remarks>
    public static EditResult<Sequence> RippleDelete(Sequence sequence, IReadOnlyList<string> clipIds)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clipIds);

        if (clipIds.Count == 0)
        {
            return EditError.NothingSelected("A ripple delete needs a clip.");
        }

        EditResult<List<(Track Track, Clip Clip)>> found = Find(sequence, clipIds);
        if (!found.IsOk)
        {
            return found.Error!;
        }

        Sequence result = sequence;
        foreach ((Track track, Clip clip) in found.Value)
        {
            result = result.ReplaceTrack(result.Track(track.Id)!.RemoveClip(clip.Id));
        }

        foreach ((TimeRange span, string[] tracks) in Enumerable.Reverse(Spans(found.Value)))
        {
            EditResult<Sequence> closed = Ripple(result, span.End, -span.Duration, tracks);
            if (!closed.IsOk)
            {
                return closed.Error!;
            }

            result = closed.Value;
        }

        return result;
    }

    /// <summary>
    /// Closes the gap on a track that a time falls in, pulling what follows back, with the
    /// sync-locked tracks.
    /// </summary>
    public static EditResult<Sequence> CloseGap(Sequence sequence, string trackId, Flicks at)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        if (sequence.Track(trackId) is not { } track)
        {
            return EditError.TrackNotFound(trackId);
        }

        if (track.Locked)
        {
            return EditError.TrackLocked(track.Name);
        }

        Flicks start = Flicks.Zero;
        foreach (Clip clip in track.Clips)
        {
            if (clip.Range.Contains(at))
            {
                return EditError.NotAGap($"'{clip.Name}' is at {Timecode.FormatClock(at)} on {track.Name}; there is no gap there.");
            }

            if (clip.Start > at)
            {
                return Ripple(sequence, clip.Start, start - clip.Start, [track.Id]);
            }

            start = clip.End;
        }

        return EditError.NotAGap($"Nothing follows {Timecode.FormatClock(at)} on {track.Name}, so there is no gap to close.");
    }

    /// <summary>
    /// Takes a range out of some tracks and leaves it empty: clips across its ends are cut there.
    /// </summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="range">The range, usually the in and out points.</param>
    /// <param name="trackIds">The tracks, or null for every track that is not locked.</param>
    public static EditResult<Sequence> LiftRange(Sequence sequence, TimeRange range, IReadOnlyCollection<string>? trackIds = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        EditResult<List<Track>> targets = Targets(sequence, trackIds);
        if (!targets.IsOk)
        {
            return targets.Error!;
        }

        if (range.Duration.Value <= 0)
        {
            return EditError.EmptyResult("There is nothing between the in and out points.");
        }

        Sequence result = sequence;
        foreach (Track track in targets.Value)
        {
            result = result.ReplaceTrack(Clear(track, range));
        }

        return result;
    }

    /// <summary>
    /// Takes a range out of some tracks and closes it up, with the sync-locked tracks.
    /// </summary>
    public static EditResult<Sequence> ExtractRange(Sequence sequence, TimeRange range, IReadOnlyCollection<string>? trackIds = null)
    {
        EditResult<Sequence> lifted = LiftRange(sequence, range, trackIds);
        if (!lifted.IsOk)
        {
            return lifted;
        }

        string[] tracks = [.. Targets(sequence, trackIds).Value.Select(track => track.Id)];
        return Ripple(lifted.Value, range.End, -range.Duration, tracks);
    }

    /// <summary>
    /// Puts new clips in at a time, pushing everything from there on later: their tracks split at
    /// the time, and the sync-locked tracks move with them.
    /// </summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="at">Where the clips go.</param>
    /// <param name="placements">The clips and their tracks. The longest decides how far things move.</param>
    public static EditResult<Sequence> Insert(Sequence sequence, Flicks at, IReadOnlyList<Placement> placements)
    {
        EditResult<List<(Track Track, Clip Clip)>> checkedPlacements = CheckPlacements(sequence, at, placements);
        if (!checkedPlacements.IsOk)
        {
            return checkedPlacements.Error!;
        }

        Flicks length = placements.Max(placement => placement.Clip.Duration);
        string[] tracks = [.. placements.Select(placement => placement.TrackId).Distinct(StringComparer.Ordinal)];

        // The tracks were checked unlocked above, and opening space fails for nothing else.
        Sequence result = Ripple(sequence, at, length, tracks).Value;
        foreach ((Track track, Clip clip) in checkedPlacements.Value)
        {
            result = result.ReplaceTrack(result.Track(track.Id)!.AddClip(clip));
        }

        return result;
    }

    /// <summary>
    /// Puts new clips in at a time over whatever is there, which is cut away. Nothing moves.
    /// </summary>
    public static EditResult<Sequence> Overwrite(Sequence sequence, Flicks at, IReadOnlyList<Placement> placements)
    {
        EditResult<List<(Track Track, Clip Clip)>> checkedPlacements = CheckPlacements(sequence, at, placements);
        if (!checkedPlacements.IsOk)
        {
            return checkedPlacements.Error!;
        }

        Sequence result = sequence;
        foreach ((Track track, Clip clip) in checkedPlacements.Value)
        {
            Track cleared = Clear(result.Track(track.Id)!, clip.Range);
            result = result.ReplaceTrack(cleared.AddClip(clip));
        }

        return result;
    }

    /// <summary>
    /// Holds the frame at a time for a while: the clip is cut there and a freeze frame of that
    /// frame is inserted, pushing the rest of the clip and everything after it later.
    /// </summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="clipId">The clip to freeze.</param>
    /// <param name="at">The frame to hold, on the timeline.</param>
    /// <param name="duration">How long to hold it.</param>
    /// <param name="holdId">The freeze frame's id, or null for a fresh one.</param>
    public static EditResult<Sequence> FreezeFrame(Sequence sequence, string clipId, Flicks at, Flicks duration, string? holdId = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        if (sequence.TrackOf(clipId) is not { } track)
        {
            return EditError.ClipNotFound(clipId);
        }

        Clip clip = track.Clip(clipId)!;
        if (!clip.Range.Contains(at))
        {
            return EditError.TimeOutOfRange(
                $"'{clip.Name}' runs from {Timecode.FormatClock(clip.Start)} to {Timecode.FormatClock(clip.End)}; {Timecode.FormatClock(at)} is not in it.");
        }

        if (duration.Value <= 0)
        {
            return EditError.EmptyResult("A freeze frame has to last for some time.");
        }

        var hold = clip with
        {
            Id = holdId ?? Id.New(),
            Range = new TimeRange(at, duration),
            SourceIn = clip.SourceTimeAt(at),
            Hold = true,
            LinkGroupId = null,
            FadeIn = null,
            FadeOut = null,
            Markers = [],
            Name = $"{clip.Name} hold",
        };

        // Insert splits the clip at the frame, and the hold goes into the space that opens.
        return Insert(sequence, at, [new Placement(track.Id, hold)]);
    }

    /// <summary>
    /// Closes every gap on the primary picture track, from the start, with the sync-locked
    /// tracks: what magnetic mode keeps true after every edit.
    /// </summary>
    public static EditResult<Sequence> Magnetize(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        if (sequence.PrimaryTrack is not { } primary || primary.Locked)
        {
            return sequence;
        }

        Sequence result = sequence;
        Flicks end = Flicks.Zero;
        int index = 0;

        while (result.Track(primary.Id)!.Clips is var clips && index < clips.Length)
        {
            Clip clip = clips[index];
            if (clip.Start > end)
            {
                EditResult<Sequence> closed = Ripple(result, clip.Start, end - clip.Start, [primary.Id]);
                if (!closed.IsOk)
                {
                    return closed;
                }

                result = closed.Value;
                continue;
            }

            end = clip.End;
            index++;
        }

        return result;
    }

    /// <summary>
    /// Moves clips on a magnetic primary track the way a storyline does: they come out, what is
    /// left closes up, and they go back in at the nearest cut to where they were dropped.
    /// </summary>
    /// <param name="sequence">The sequence, magnetic or not.</param>
    /// <param name="clipIds">The clips to move, all on the primary track.</param>
    /// <param name="to">Where the first of them was dropped.</param>
    public static EditResult<Sequence> MoveOnStoryline(Sequence sequence, IReadOnlyList<string> clipIds, Flicks to)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clipIds);

        if (sequence.PrimaryTrack is not { } primary)
        {
            return EditError.TrackNotFound("V1");
        }

        EditResult<List<(Track Track, Clip Clip)>> found = Find(sequence, clipIds);
        if (!found.IsOk)
        {
            return found.Error!;
        }

        if (found.Value.Any(entry => !string.Equals(entry.Track.Id, primary.Id, StringComparison.Ordinal)))
        {
            return EditError.NotAligned($"Only clips on {primary.Name} move along the storyline.");
        }

        Clip[] moving = [.. found.Value.Select(entry => entry.Clip).OrderBy(clip => clip.Start)];
        EditResult<Sequence> removed = RippleDelete(sequence, [.. moving.Select(clip => clip.Id)]);
        if (!removed.IsOk)
        {
            return removed;
        }

        // The nearest cut on what is left: the start of a clip, or the end of the last one.
        Track remaining = removed.Value.Track(primary.Id)!;
        Flicks cut = Flicks.Zero;
        foreach (Flicks edit in remaining.Clips.Select(clip => clip.Start).Append(remaining.Duration))
        {
            if (Distance(edit, to) < Distance(cut, to))
            {
                cut = edit;
            }
        }

        Flicks offset = Flicks.Zero;
        var placements = new List<Placement>(moving.Length);
        foreach (Clip clip in moving)
        {
            placements.Add(new Placement(primary.Id, clip with { Range = new TimeRange(cut + offset, clip.Duration) }));
            offset += clip.Duration;
        }

        // One insert of the whole run, so the clips keep their order and sit end to end.
        Clip run = moving[0] with { Range = new TimeRange(cut, offset) };
        // The primary track was unlocked for the delete and the run has a length, so this holds.
        Sequence opened = Insert(removed.Value, cut, [new Placement(primary.Id, run)]).Value;
        Track withRun = opened.Track(primary.Id)!.RemoveClip(run.Id);
        foreach (Placement placement in placements)
        {
            withRun = withRun.AddClip(placement.Clip);
        }

        return opened.ReplaceTrack(withRun);
    }

    /// <summary>
    /// Every clip id whose clip changed, appeared or went between two versions of a sequence, and
    /// the tracks they were on: what a handler reports.
    /// </summary>
    public static ImmutableArray<string> Changed(Sequence before, Sequence after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changed = new List<string>();
        foreach (Track track in after.Tracks)
        {
            Track? old = before.Track(track.Id);
            if (old is not null && old.Clips == track.Clips)
            {
                continue;
            }

            changed.Add(track.Id);
            var had = (old?.Clips ?? []).ToDictionary(clip => clip.Id, StringComparer.Ordinal);
            foreach (Clip clip in track.Clips)
            {
                if (!had.Remove(clip.Id, out Clip? was) || was != clip)
                {
                    changed.Add(clip.Id);
                }
            }

            changed.AddRange(had.Keys);
        }

        return [.. changed.Distinct(StringComparer.Ordinal)];
    }

    private static Flicks Distance(Flicks a, Flicks b) => a > b ? a - b : b - a;

    /// <summary>Opens space at a time on one track, splitting a clip that straddles it.</summary>
    private static Track Open(Track track, Flicks at, Flicks delta)
    {
        Track result = track;
        foreach (Clip clip in track.Clips)
        {
            if (clip.Start >= at)
            {
                result = result.ReplaceClip(clip with { Range = clip.Range.Shift(delta) });
            }
            else if (clip.End > at)
            {
                Track split = Split(result, clip.Id, at).Value;
                Clip right = split.Clips.First(candidate => candidate.Start == at);
                result = split.ReplaceClip(right with { Range = right.Range.Shift(delta) });
            }
        }

        return result;
    }

    /// <summary>Closes the span just before a time on one track, which must be empty.</summary>
    private static EditResult<Track> Close(Track track, Flicks at, Flicks delta, bool edited)
    {
        var span = TimeRange.FromBounds(at + delta, at);
        Track result = track;

        foreach (Clip clip in track.Clips)
        {
            if (clip.Range.Intersects(span))
            {
                return edited
                    ? EditError.WouldOverlap($"'{clip.Name}' on {track.Name} is in the way of the ripple.")
                    : EditError.SyncLockBlocked(track.Name, clip.Name);
            }

            if (clip.Start >= at)
            {
                result = result.ReplaceClip(clip with { Range = clip.Range.Shift(delta) });
            }
        }

        return result;
    }

    /// <summary>Cuts a track's clips at a range's ends and takes out what is inside.</summary>
    private static Track Clear(Track track, TimeRange range)
    {
        Track result = track;
        foreach (Clip clip in track.Clips)
        {
            if (!clip.Range.Intersects(range))
            {
                continue;
            }

            Track working = result;
            string inside = clip.Id;

            if (clip.Start < range.Start)
            {
                working = Split(working, clip.Id, range.Start).Value;
                inside = working.Clips.First(candidate => candidate.Start == range.Start).Id;
            }

            if (clip.End > range.End)
            {
                working = Split(working, inside, range.End).Value;
            }

            result = working.RemoveClip(inside);
        }

        return result;
    }

    /// <summary>The tracks an operation works on: those named, or every unlocked one.</summary>
    private static EditResult<List<Track>> Targets(Sequence sequence, IReadOnlyCollection<string>? trackIds)
    {
        if (trackIds is null)
        {
            return sequence.Tracks.Where(track => !track.Locked).ToList();
        }

        var tracks = new List<Track>(trackIds.Count);
        foreach (string id in trackIds)
        {
            if (sequence.Track(id) is not { } track)
            {
                return EditError.TrackNotFound(id);
            }

            if (track.Locked)
            {
                return EditError.TrackLocked(track.Name);
            }

            tracks.Add(track);
        }

        return tracks;
    }

    /// <summary>Each clip with its track, refusing unknown clips and locked tracks.</summary>
    private static EditResult<List<(Track Track, Clip Clip)>> Find(Sequence sequence, IReadOnlyList<string> clipIds)
    {
        var found = new List<(Track, Clip)>(clipIds.Count);
        foreach (string clipId in clipIds.Distinct(StringComparer.Ordinal))
        {
            if (sequence.TrackOf(clipId) is not { } track)
            {
                return EditError.ClipNotFound(clipId);
            }

            if (track.Locked)
            {
                return EditError.TrackLocked(track.Name);
            }

            found.Add((track, track.Clip(clipId)!));
        }

        return found;
    }

    /// <summary>
    /// The clips' ranges joined into spans where they overlap or touch, with the tracks each
    /// span's clips were on, earliest first.
    /// </summary>
    private static List<(TimeRange Span, string[] Tracks)> Spans(List<(Track Track, Clip Clip)> clips)
    {
        var spans = new List<(TimeRange Span, HashSet<string> Tracks)>();
        foreach ((Track track, Clip clip) in clips.OrderBy(entry => entry.Clip.Start))
        {
            if (spans.Count > 0 && clip.Start <= spans[^1].Span.End)
            {
                (TimeRange span, HashSet<string> tracks) = spans[^1];
                tracks.Add(track.Id);
                spans[^1] = (TimeRange.FromBounds(span.Start, Flicks.Max(span.End, clip.End)), tracks);
            }
            else
            {
                spans.Add((clip.Range, new HashSet<string>(StringComparer.Ordinal) { track.Id }));
            }
        }

        return [.. spans.Select(entry => (entry.Span, entry.Tracks.ToArray()))];
    }

    /// <summary>Checks placements and positions each clip at the edit's time.</summary>
    private static EditResult<List<(Track Track, Clip Clip)>> CheckPlacements(Sequence sequence, Flicks at, IReadOnlyList<Placement> placements)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(placements);

        if (placements.Count == 0)
        {
            return EditError.NothingSelected("There is nothing to put in.");
        }

        if (at.IsNegative)
        {
            return EditError.TimeOutOfRange("A clip cannot start before the beginning of the sequence.");
        }

        var placed = new List<(Track, Clip)>(placements.Count);
        foreach (Placement placement in placements)
        {
            if (sequence.Track(placement.TrackId) is not { } track)
            {
                return EditError.TrackNotFound(placement.TrackId);
            }

            if (track.Locked)
            {
                return EditError.TrackLocked(track.Name);
            }

            if (placement.Clip.Duration.Value <= 0)
            {
                return EditError.EmptyResult($"'{placement.Clip.Name}' has no length.");
            }

            placed.Add((track, placement.Clip with { Range = new TimeRange(at, placement.Clip.Duration) }));
        }

        return placed;
    }
}
