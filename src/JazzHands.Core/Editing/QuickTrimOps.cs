using System.Collections.Immutable;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Editing;

/// <summary>
/// The Quick Trim layout: one file where it sits in the source, its kept stretches as clips.
/// </summary>
/// <remarks>
/// A Quick Trim sequence has V1 for the picture and one audio track per sound stream of the file,
/// in stream order. Every clip sits at the timeline time equal to its source time, so a kept
/// stretch is a clip on each of those tracks, linked, and what was cut away is a gap. Everything
/// here is pure: the handlers and the Quick Trim view call it, and the tests need no engine.
///
/// Stretches are always whole frames of the sequence, sorted, and never touch: two that meet are
/// one. That is what <see cref="Normalize"/> makes of any list, so "keep 10 to 25 and 20 to 30"
/// is "keep 10 to 30" whichever surface asked.
/// </remarks>
public static class QuickTrimOps
{
    /// <summary>The kept stretches of a Quick Trim sequence: its picture clips, joined where they meet.</summary>
    public static ImmutableArray<TimeRange> Segments(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        Track? picture = PictureTrack(sequence);
        if (picture is null)
        {
            return [];
        }

        var joined = ImmutableArray.CreateBuilder<TimeRange>();
        foreach (Clip clip in picture.Clips)
        {
            if (joined.Count > 0 && joined[^1].End == clip.Start)
            {
                joined[^1] = TimeRange.FromBounds(joined[^1].Start, clip.End);
            }
            else
            {
                joined.Add(clip.Range);
            }
        }

        return joined.ToImmutable();
    }

    /// <summary>
    /// Puts ranges on the frame grid, inside the file, in order, with overlapping and touching
    /// ones joined and empty ones dropped.
    /// </summary>
    /// <param name="ranges">What was asked for, in any order.</param>
    /// <param name="duration">How long the file is.</param>
    /// <param name="frameRate">The sequence's frame rate.</param>
    public static ImmutableArray<TimeRange> Normalize(IEnumerable<TimeRange> ranges, Flicks duration, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        Flicks end = duration.SnapToFrame(frameRate, RoundingMode.Nearest);
        var snapped = new List<TimeRange>();

        foreach (TimeRange range in ranges)
        {
            Flicks start = Flicks.Max(Flicks.Zero, range.Start.SnapToFrame(frameRate, RoundingMode.Nearest));
            Flicks stop = Flicks.Min(end, range.End.SnapToFrame(frameRate, RoundingMode.Nearest));
            if (stop > start)
            {
                snapped.Add(TimeRange.FromBounds(start, stop));
            }
        }

        snapped.Sort();

        var merged = ImmutableArray.CreateBuilder<TimeRange>(snapped.Count);
        foreach (TimeRange range in snapped)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                merged[^1] = TimeRange.FromBounds(merged[^1].Start, Flicks.Max(merged[^1].End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged.ToImmutable();
    }

    /// <summary>The stretches with one more kept.</summary>
    public static ImmutableArray<TimeRange> Keep(IEnumerable<TimeRange> segments, TimeRange range, Flicks duration, Rational frameRate) =>
        Normalize([.. segments, range], duration, frameRate);

    /// <summary>The stretches with a range cut out of whichever of them it crosses.</summary>
    public static ImmutableArray<TimeRange> Cut(IEnumerable<TimeRange> segments, TimeRange range, Flicks duration, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(segments);

        Flicks from = range.Start.SnapToFrame(frameRate, RoundingMode.Nearest);
        Flicks to = range.End.SnapToFrame(frameRate, RoundingMode.Nearest);
        var left = new List<TimeRange>();

        foreach (TimeRange segment in segments)
        {
            if (segment.End <= from || segment.Start >= to)
            {
                left.Add(segment);
                continue;
            }

            if (segment.Start < from)
            {
                left.Add(TimeRange.FromBounds(segment.Start, from));
            }

            if (segment.End > to)
            {
                left.Add(TimeRange.FromBounds(to, segment.End));
            }
        }

        return Normalize(left, duration, frameRate);
    }

    /// <summary>
    /// Makes a new Quick Trim sequence for a file, keeping all of it.
    /// </summary>
    /// <param name="id">The sequence id.</param>
    /// <param name="name">Its name.</param>
    /// <param name="media">The file. It must have been probed and have a picture.</param>
    /// <param name="defaults">The project settings, for the sound format.</param>
    /// <param name="newId">Makes identifiers, so tests can make predictable ones.</param>
    public static Sequence Create(string id, string name, MediaItem media, ProjectSettings defaults, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(newId);

        MediaStream video = VideoStream(media)
            ?? throw new ArgumentException($"'{media.Name}' has no picture to trim.", nameof(media));

        var tracks = ImmutableArray.CreateBuilder<Track>();
        tracks.Add(new Track(newId(), TrackKind.Video, "V1", 0));

        int order = 1;
        foreach (MediaStream stream in media.Info!.AudioStreams)
        {
            string trackName = stream.Title is { Length: > 0 } title ? title : $"A{order}";
            tracks.Add(new Track(newId(), TrackKind.Audio, trackName, order));
            order++;
        }

        Rational rate = video.FrameRate ?? defaults.FrameRate;
        var settings = defaults with
        {
            FrameRate = rate,
            Width = video.Width > 0 ? video.Width : defaults.Width,
            Height = video.Height > 0 ? video.Height : defaults.Height,
        };

        var sequence = new Sequence(
            id,
            name,
            new EquatableArray<Track>(tracks.ToImmutable()),
            Settings: settings,
            QuickTrim: new QuickTrim(media.Id));

        return Layout(sequence, media, Normalize([new TimeRange(Flicks.Zero, media.Duration)], media.Duration, rate), newId);
    }

    /// <summary>
    /// Lays a Quick Trim sequence out with exactly these stretches kept.
    /// </summary>
    /// <remarks>
    /// A stretch that is already there keeps its clips, ids and all, so keeping one more stretch
    /// does not disturb the others and a selection survives. A new stretch's sound clips are on
    /// or off as the clips already on their track are, so a stream muted clip by clip stays
    /// muted in stretches added after.
    /// </remarks>
    /// <param name="sequence">The Quick Trim sequence.</param>
    /// <param name="media">The file it trims.</param>
    /// <param name="segments">The stretches, already normalized.</param>
    /// <param name="newId">Makes identifiers for new clips.</param>
    public static Sequence Layout(Sequence sequence, MediaItem media, IReadOnlyList<TimeRange> segments, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(newId);

        Track picture = PictureTrack(sequence)
            ?? throw new ArgumentException("A Quick Trim sequence needs a picture track.", nameof(sequence));
        MediaStream video = VideoStream(media)
            ?? throw new ArgumentException($"'{media.Name}' has no picture to trim.", nameof(media));

        List<(Track Track, int Stream, bool Enabled)> sound = SoundTracks(sequence, media);

        var pictureClips = new List<Clip>();
        var soundClips = sound.Select(_ => new List<Clip>()).ToArray();

        foreach (TimeRange segment in segments)
        {
            Clip? kept = picture.Clips.FirstOrDefault(clip => clip.Range == segment && clip.SourceIn == segment.Start);
            string link = kept?.LinkGroupId ?? newId();

            pictureClips.Add(kept ?? new Clip(
                newId(),
                segment,
                segment.Start,
                MediaId: media.Id,
                SourceStreamIndex: video.Index,
                LinkGroupId: sound.Count > 0 ? link : null,
                Name: media.Name));

            for (int index = 0; index < sound.Count; index++)
            {
                (Track track, int stream, bool enabled) = sound[index];
                Clip? existing = kept?.LinkGroupId is { } group
                    ? track.Clips.FirstOrDefault(clip => clip.LinkGroupId == group && clip.Range == segment)
                    : null;

                soundClips[index].Add(existing ?? new Clip(
                    newId(),
                    segment,
                    segment.Start,
                    MediaId: media.Id,
                    SourceStreamIndex: stream,
                    Enabled: enabled,
                    LinkGroupId: link,
                    Name: media.Name));
            }
        }

        Sequence result = sequence.ReplaceTrack(picture with { Clips = new EquatableArray<Clip>([.. pictureClips]) });
        for (int index = 0; index < sound.Count; index++)
        {
            result = result.ReplaceTrack(sound[index].Track with { Clips = new EquatableArray<Clip>([.. soundClips[index]]) });
        }

        return result;
    }

    /// <summary>The track the picture is on: the lowest video track.</summary>
    public static Track? PictureTrack(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.Tracks.Where(track => track.Kind == TrackKind.Video).OrderBy(track => track.Order).FirstOrDefault();
    }

    /// <summary>The picture stream a Quick Trim plays.</summary>
    public static MediaStream? VideoStream(MediaItem media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return media.Kind == MediaKind.Movie ? media.Info?.VideoStreams.FirstOrDefault() : null;
    }

    /// <summary>
    /// Each audio track with the stream it plays: the one its clips already play, or else the
    /// stream in the same place among the file's sound streams as the track among the audio
    /// tracks.
    /// </summary>
    public static List<(Track Track, int Stream, bool Enabled)> SoundTracks(Sequence sequence, MediaItem media)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(media);

        MediaStream[] streams = [.. media.Info?.AudioStreams ?? []];
        var result = new List<(Track, int, bool)>();
        int position = 0;

        foreach (Track track in sequence.Tracks.Where(track => track.Kind == TrackKind.Audio).OrderBy(track => track.Order))
        {
            Clip? any = track.Clips.IsEmpty ? null : track.Clips[0];
            int? stream = any?.SourceStreamIndex ?? (position < streams.Length ? streams[position].Index : null);
            position++;

            if (stream is { } index)
            {
                result.Add((track, index, any?.Enabled ?? true));
            }
        }

        return result;
    }
}
