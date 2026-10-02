using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Editing;

/// <summary>Where a three-point edit puts what, once the marks are read.</summary>
/// <param name="SourceIn">Where the new clip starts in its file.</param>
/// <param name="Duration">How long it runs.</param>
/// <param name="At">Where it starts on the timeline.</param>
/// <param name="Note">What was decided for the person, such as a four-point edit fitted to the shorter; null when nothing needs saying.</param>
public sealed record ThreePoint(Flicks SourceIn, Flicks Duration, Flicks At, string? Note = null);

/// <summary>
/// Three-point editing: from the source's marks, the sequence's and the playhead, the stretch of
/// a file that goes in and where; and which tracks it goes to.
/// </summary>
/// <remarks>
/// <para>
/// The rules every editor knows. Two source marks and a sequence in point (or the playhead) put
/// the marked stretch there. A sequence in and out and one source mark fill the sequence range
/// from the source in, or up to the source out (backtimed). All four fit the shorter of the two
/// ranges, and say so. With no source marks the item's own range is used (a subclip's), with no
/// sequence marks the playhead.
/// </para>
/// <para>
/// A sequence's in and out are one range (<see cref="Sequence.InOut"/>), and setting the in point
/// alone gives it an open out at the end of the sequence. So the out counts as a mark only
/// inside the sequence, before its last clip ends: an in point marked at the end of the timeline
/// to add to it is a three-point edit, not a one frame four-point one.
/// </para>
/// </remarks>
public static class ThreePointOps
{
    /// <summary>Works out a three-point edit.</summary>
    /// <param name="sourceIn">The source monitor's in mark, or null.</param>
    /// <param name="sourceOut">Its out mark, exclusive, or null.</param>
    /// <param name="media">The file being edited in: its default range and its length.</param>
    /// <param name="sequenceRange">The sequence's in and out, or null.</param>
    /// <param name="sequenceEnd">Where the sequence's last clip ends.</param>
    /// <param name="playhead">The sequence playhead.</param>
    public static EditResult<ThreePoint> Resolve(
        Flicks? sourceIn,
        Flicks? sourceOut,
        MediaItem media,
        TimeRange? sequenceRange,
        Flicks sequenceEnd,
        Flicks playhead)
    {
        ArgumentNullException.ThrowIfNull(media);

        TimeRange? marked = sequenceRange is { } range && range.End < sequenceEnd ? range : null;
        Flicks at = sequenceRange?.Start ?? playhead;
        if (at < Flicks.Zero)
        {
            return EditError.TimeOutOfRange("An edit cannot start before the sequence does.");
        }

        ThreePoint edit;
        if (sourceIn is { } from && sourceOut is { } to)
        {
            Flicks length = to - from;
            edit = marked is { } both && both.Duration != length
                ? new ThreePoint(from, Flicks.Min(length, both.Duration), at, FourPoints(length, both.Duration))
                : new ThreePoint(from, length, at);
        }
        else if (sourceOut is { } end)
        {
            // Backtimed: the sequence range ends on the source out.
            edit = marked is { } filled
                ? new ThreePoint(end - filled.Duration, filled.Duration, at)
                : new ThreePoint(media.DefaultIn, end - media.DefaultIn, at);
        }
        else
        {
            Flicks start = sourceIn ?? media.DefaultIn;
            edit = new ThreePoint(start, marked?.Duration ?? (media.DefaultOut - start), at);
        }

        if (edit.Duration <= Flicks.Zero)
        {
            return EditError.EmptyResult("The source out is not after its in, so there is nothing to edit in.");
        }

        if (edit.SourceIn < Flicks.Zero || edit.SourceIn + edit.Duration > media.Duration)
        {
            return EditError.NoSourceLeft(
                $"'{media.Name}' has not got enough to fill {Timecode.FormatClock(edit.Duration)} from there; move the source marks or the sequence's.");
        }

        return edit;
    }

    private static string FourPoints(Flicks source, Flicks sequence) =>
        $"Four marks: the source's {Timecode.FormatClock(source)} and the sequence's {Timecode.FormatClock(sequence)} differ, so the shorter, {Timecode.FormatClock(Flicks.Min(source, sequence))}, went in.";

    /// <summary>
    /// The tracks an edit from the source goes to: the picture's, and one per sound stream in
    /// order (null past the last targeted audio track). With no patch set, the lowest unlocked
    /// video track and every unlocked audio track, lowest first.
    /// </summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="audioStreams">How many sound streams the source has.</param>
    public static (Track? Picture, Track?[] Sound) Targets(Sequence sequence, int audioStreams)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        Track[] targeted = [.. TargetedTracks(sequence)];
        Track? picture = targeted.FirstOrDefault(track => track.Kind == TrackKind.Video);
        Track[] sound = [.. targeted.Where(track => track.Kind == TrackKind.Audio)];
        var streams = new Track?[Math.Max(0, audioStreams)];
        for (int stream = 0; stream < streams.Length && stream < sound.Length; stream++)
        {
            streams[stream] = sound[stream];
        }

        return (picture, streams);
    }

    /// <summary>The tracks targeted now, in stacking order: the patch's, or the defaults.</summary>
    public static IEnumerable<Track> TargetedTracks(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        IEnumerable<Track> ordered = sequence.Tracks.OrderBy(track => track.Order).Where(track => !track.Locked);
        if (sequence.SourcePatch is { } patch)
        {
            return ordered.Where(track => patch.Targets.Contains(track.Id));
        }

        Track? picture = ordered.FirstOrDefault(track => track.Kind == TrackKind.Video);
        return ordered.Where(track => track == picture || track.Kind == TrackKind.Audio);
    }

    /// <summary>
    /// The sequence with a track targeted or not. Targeting a picture track untargets the other
    /// picture tracks, since one picture goes in at a time.
    /// </summary>
    public static Sequence SetTarget(Sequence sequence, Track track, bool on)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);

        List<Track> targets = [.. TargetedTracks(sequence)];
        if (on)
        {
            if (track.Kind == TrackKind.Video)
            {
                targets.RemoveAll(candidate => candidate.Kind == TrackKind.Video);
            }

            if (!targets.Exists(candidate => candidate.Id == track.Id))
            {
                targets.Add(track);
            }
        }
        else
        {
            targets.RemoveAll(candidate => candidate.Id == track.Id);
        }

        return sequence with { SourcePatch = new SourcePatch([.. targets.Select(target => target.Id)]) };
    }
}
