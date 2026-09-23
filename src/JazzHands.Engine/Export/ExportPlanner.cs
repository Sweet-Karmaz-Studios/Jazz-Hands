using System.Collections.Immutable;
using System.Globalization;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;
using JazzHands.Media.Encode;

namespace JazzHands.Engine.Export;

/// <summary>
/// Turns an export request into a plan: copy or encode, which stretches, which streams, which
/// encoders, and why.
/// </summary>
/// <remarks>
/// A copy is possible when the picture is one file played as it is (normal speed, forwards, no
/// effects, no transform, no crop, no masks) with nothing over it, and every sound lane is one of
/// that file's streams played with its picture and nothing done to it. A muted lane is left out.
/// Anything else is rendered and encoded, and each thing that stood in the way is a sentence in
/// the plan's reasons.
///
/// A copy cuts on keyframes. Asked for a copy whose cuts are not on keyframes, the planner
/// refuses and names the nearest ones, unless told to snap, in which case it moves each cut to
/// the nearest keyframe and reports every move with the frame it landed on. Auto never snaps by
/// itself: it encodes instead, so a cut is exactly where it was put unless someone said otherwise.
///
/// Refusals are <see cref="CommandException"/>s, so the CLI, the queue and the dialog all say the
/// same thing.
/// </remarks>
public static class ExportPlanner
{
    /// <summary>Plans an export.</summary>
    /// <param name="project">The project as it is now.</param>
    /// <param name="projectPath">Where it lives, or empty for an unsaved one.</param>
    /// <param name="request">What was asked for.</param>
    /// <param name="keyframes">Where keyframe indexes come from.</param>
    /// <param name="cancellationToken">Stops a keyframe scan.</param>
    public static ExportPlan Plan(
        Project project,
        string projectPath,
        ExportRequest request,
        KeyframeLookup keyframes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(keyframes);

        ExportPresetInfo preset = ExportPresets.Find(request.Preset)
            ?? throw new CommandException(
                "unknown-preset",
                $"There is no preset called '{request.Preset}'. The presets are {string.Join(", ", ExportPresets.All.Select(p => p.Name))}.");

        Sequence sequence = (request.SequenceId is { } id ? project.Sequence(id) : project.ActiveSequence)
            ?? throw new CommandException("sequence-not-found", request.SequenceId is null
                ? "The project has no sequence to export."
                : $"No sequence with id '{request.SequenceId}'.");

        ProjectSettings settings = project.SettingsFor(sequence);
        (string output, string container) = Output(request.OutputPath, projectPath, preset);
        ImmutableArray<TimeRange> ranges = Ranges(sequence, request.UseInOut);

        if (ranges.IsEmpty)
        {
            throw new CommandException(
                "nothing-to-export",
                sequence.QuickTrim is not null
                    ? $"'{sequence.Name}' keeps nothing. Keep a stretch before exporting."
                    : $"'{sequence.Name}' is empty.");
        }

        RequireMediaOnline(project, projectPath, sequence, ranges);

        var reasons = new List<string>();
        CopySource? copy = CopyCheck(project, projectPath, sequence, ranges, reasons);

        if (request.Mode == ExportMode.Copy && copy is null)
        {
            throw new CommandException(
                "cannot-copy",
                $"This cannot be exported by stream copy: {string.Join(" ", reasons)} Export with --mode encode instead.");
        }

        if (copy is not null && request.Mode != ExportMode.Encode)
        {
            if (request.Mode == ExportMode.Auto && !PresetMatches(preset, copy, settings, reasons))
            {
                copy = null;
            }
        }
        else
        {
            copy = null;
        }

        if (copy is not null)
        {
            KeyframeIndex index = keyframes.Get(copy.Media, copy.Path, copy.VideoStream, cancellationToken);
            var snaps = new List<KeyframeSnap>();
            ImmutableArray<TimeRange> snapped = Snap(copy, index, snaps);
            bool moved = snaps.Count > 0;

            if (moved && !request.SnapToKeyframes)
            {
                KeyframeSnap first = snaps[0];
                string where = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The {first.Edge} at {Timecode.FormatClock(first.Requested)} is not on a keyframe; the nearest is {Timecode.FormatClock(first.Snapped)} (frame {first.Frame}).");

                if (request.Mode == ExportMode.Copy)
                {
                    throw new CommandException(
                        "cut-not-on-keyframe",
                        $"{where} A stream copy can only cut on keyframes. Pass --snap-to-keyframes to move the cuts there, or export with --mode encode to cut exactly.");
                }

                reasons.Add($"{where} Encoding so every cut lands exactly where it was put; ask for a copy with snapping to keep the source untouched.");
                copy = null;
            }
            else if (snapped.IsEmpty)
            {
                throw new CommandException(
                    "nothing-to-export",
                    "Every stretch is shorter than the distance between two keyframes, so a copy would keep nothing. Export with --mode encode.");
            }
            else
            {
                return CopyPlan(request, preset, sequence, output, container, ranges, copy, snapped, snaps, reasons);
            }
        }

        return EncodePlan(request, preset, sequence, settings, output, container, ranges, project, reasons);
    }

    /// <summary>The stretches of the sequence an export plays, back to back, in sequence time.</summary>
    public static ImmutableArray<TimeRange> Ranges(Sequence sequence, bool useInOut)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        ImmutableArray<TimeRange> whole = sequence.QuickTrim is not null
            ? QuickTrimOps.Segments(sequence)
            : sequence.Duration > Flicks.Zero ? [new TimeRange(Flicks.Zero, sequence.Duration)] : [];

        if (!useInOut)
        {
            return whole;
        }

        TimeRange inOut = sequence.InOut
            ?? throw new CommandException("no-in-out", $"'{sequence.Name}' has no in and out points to export between.");

        return [.. whole.Where(range => range.Intersects(inOut)).Select(range => range.Intersect(inOut)).Where(range => !range.IsEmpty)];
    }

    private static (string Path, string Container) Output(string requested, string projectPath, ExportPresetInfo preset)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new CommandException("missing-output", "Say where the file should go.");
        }

        string path = requested;
        if (!Path.IsPathRooted(path) && projectPath.Length > 0)
        {
            path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".", path);
        }

        path = Path.GetFullPath(path);
        if (Path.GetExtension(path).Length == 0)
        {
            path += preset.Extension;
        }

        string container = Muxer.FormatForExtension(Path.GetExtension(path))
            ?? throw new CommandException(
                "unsupported-container",
                $"Jazz Hands does not write '{Path.GetExtension(path)}' files. Use .mp4, .mov or .mkv.");

        if (container == "webm")
        {
            throw new CommandException(
                "unsupported-container",
                "WebM needs VP9 or AV1, which arrive with the full export engine. Use .mp4 or .mkv.");
        }

        if (Directory.Exists(path))
        {
            throw new CommandException("output-is-folder", $"'{path}' is a folder. Name a file.");
        }

        return (path, container);
    }

    private static void RequireMediaOnline(Project project, string projectPath, Sequence sequence, ImmutableArray<TimeRange> ranges)
    {
        foreach (Track track in sequence.Tracks)
        {
            foreach (Clip clip in track.Clips)
            {
                if (clip.MediaId is not { } mediaId || !ranges.Any(range => range.Intersects(clip.Range)))
                {
                    continue;
                }

                MediaItem media = project.MediaItem(mediaId)
                    ?? throw new CommandException("missing-media-reference", $"Clip '{clip.Name}' plays media '{mediaId}', which is not in the project.");

                if (media.IsImages)
                {
                    continue;
                }

                string path = PathOf(media, projectPath);
                if (!File.Exists(path))
                {
                    throw new CommandException(
                        "media-offline",
                        $"'{media.Name}' is not at '{path}'. Relink it before exporting.");
                }
            }
        }
    }

    /// <summary>The full path of a media item's file.</summary>
    public static string PathOf(MediaItem media, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(media);
        return projectPath.Length == 0 ? Path.GetFullPath(media.RelativePath) : ProjectPaths.Resolve(projectPath, media.RelativePath);
    }

    /// <summary>
    /// What a copy would take, or null with the reasons it cannot. Stretches are in source time
    /// and not yet on keyframes.
    /// </summary>
    private static CopySource? CopyCheck(
        Project project,
        string projectPath,
        Sequence sequence,
        ImmutableArray<TimeRange> ranges,
        List<string> reasons)
    {
        int before = reasons.Count;
        bool anySolo = sequence.Tracks.Any(track => track.Solo);
        bool Audible(Track track) => !track.Muted && (!anySolo || track.Solo || !track.IsAudio);

        Track[] pictures = [.. sequence.Tracks.Where(track =>
            track.Kind != TrackKind.Audio && !track.Muted && Within(track, ranges).Any())];

        if (pictures.Length == 0)
        {
            reasons.Add("There is no picture to copy.");
            return null;
        }

        if (pictures.Length > 1 || pictures[0].Kind != TrackKind.Video)
        {
            reasons.Add("More than one track has pictures, and layers have to be composited.");
            return null;
        }

        Track picture = pictures[0];
        Clip[] clips = [.. Within(picture, ranges)];
        string? mediaId = clips[0].MediaId;

        foreach (Clip clip in clips)
        {
            string? why = PictureProblem(clip, mediaId);
            if (why is not null)
            {
                reasons.Add(why);
            }
        }

        if (!picture.Effects.IsEmpty)
        {
            reasons.Add($"{picture.Name} has effects.");
        }

        if (reasons.Count > before || mediaId is null)
        {
            return null;
        }

        MediaItem media = project.MediaItem(mediaId)!;
        MediaStream? video = media.Info?.Streams.FirstOrDefault(stream => stream.Index == clips[0].SourceStreamIndex);
        if (media.Kind != MediaKind.Movie || video is null || video.Kind != MediaStreamKind.Video)
        {
            reasons.Add($"'{media.Name}' is not a movie with a picture stream to copy.");
            return null;
        }

        if (media.ShouldDeinterlace)
        {
            reasons.Add($"'{media.Name}' is deinterlaced on the way in, and a copy would keep it interlaced.");
            return null;
        }

        // Every stretch has to be covered by the picture, without gaps, to be copied.
        ImmutableArray<TimeRange> source = SourceStretches(picture, ranges, reasons);
        if (reasons.Count > before)
        {
            return null;
        }

        var streams = new List<(int Stream, string Name)>();
        foreach (Track track in sequence.Tracks.Where(track => track.IsAudio).OrderBy(track => track.Order))
        {
            Clip[] sound = [.. Within(track, ranges)];
            if (sound.Length == 0)
            {
                continue;
            }

            if (!Audible(track) || sound.All(clip => !clip.Enabled))
            {
                continue;
            }

            int? stream = SoundStream(track, sound, clips, mediaId, reasons);
            if (stream is { } index)
            {
                streams.Add((index, track.Name));
            }
        }

        if (reasons.Count > before)
        {
            return null;
        }

        return new CopySource(
            media,
            PathOf(media, projectPath),
            video.Index,
            video,
            [.. streams.Select(stream => stream.Stream)],
            [.. streams.Select(stream => stream.Name)],
            source);
    }

    private static IEnumerable<Clip> Within(Track track, ImmutableArray<TimeRange> ranges) =>
        track.Clips.Where(clip => ranges.Any(range => range.Intersects(clip.Range)));

    private static string? PictureProblem(Clip clip, string? mediaId)
    {
        if (!clip.IsMedia)
        {
            return $"'{clip.Name}' is not from a file.";
        }

        if (!string.Equals(clip.MediaId, mediaId, StringComparison.Ordinal))
        {
            return "The picture comes from more than one file.";
        }

        if (!clip.Enabled)
        {
            return $"'{clip.Name}' is switched off, which leaves a black gap.";
        }

        if (clip.EffectiveSpeed != Rational.One || clip.Reverse)
        {
            return $"'{clip.Name}' plays at another speed or backwards.";
        }

        if (!clip.Effects.IsEmpty)
        {
            return $"'{clip.Name}' has effects.";
        }

        if (clip.Transform is not null || clip.Opacity is not null || clip.Crop is not null || !clip.Masks.IsEmpty
            || clip.BlendMode != BlendMode.Normal)
        {
            return $"'{clip.Name}' is moved, scaled, cropped, masked or faded.";
        }

        return null;
    }

    /// <summary>The source stretches the picture plays for the ranges, joined where they run on.</summary>
    private static ImmutableArray<TimeRange> SourceStretches(Track picture, ImmutableArray<TimeRange> ranges, List<string> reasons)
    {
        var stretches = new List<TimeRange>();

        foreach (TimeRange range in ranges)
        {
            Flicks at = range.Start;
            foreach (Clip clip in picture.Clips.Where(clip => clip.Range.Intersects(range)))
            {
                if (clip.Start > at)
                {
                    reasons.Add($"There is a gap in the picture at {Timecode.FormatClock(at)}, which would be black.");
                    return [];
                }

                Flicks end = Flicks.Min(clip.End, range.End);
                TimeRange piece = TimeRange.FromBounds(clip.SourceTimeAt(at), clip.SourceTimeAt(end));

                if (stretches.Count > 0 && stretches[^1].End == piece.Start)
                {
                    stretches[^1] = TimeRange.FromBounds(stretches[^1].Start, piece.End);
                }
                else
                {
                    stretches.Add(piece);
                }

                at = end;
            }

            if (at < range.End)
            {
                reasons.Add($"There is a gap in the picture at {Timecode.FormatClock(at)}, which would be black.");
                return [];
            }
        }

        return [.. stretches];
    }

    /// <summary>The stream a sound lane plays, when it is a plain copy of one of the picture's streams.</summary>
    private static int? SoundStream(Track track, Clip[] sound, Clip[] pictures, string mediaId, List<string> reasons)
    {
        if (track.Volume is not null || track.Pan is not null || !track.Effects.IsEmpty)
        {
            reasons.Add($"{track.Name} has its level, pan or effects changed.");
            return null;
        }

        if (sound.Any(clip => !clip.Enabled) && sound.Any(clip => clip.Enabled))
        {
            reasons.Add($"{track.Name} is muted in some places and not others.");
            return null;
        }

        int stream = sound[0].SourceStreamIndex;
        foreach (Clip clip in sound)
        {
            if (!string.Equals(clip.MediaId, mediaId, StringComparison.Ordinal) || clip.SourceStreamIndex != stream)
            {
                reasons.Add($"{track.Name} plays sound from somewhere other than the picture's file.");
                return null;
            }

            if (clip.Volume is not null || clip.Pan is not null || clip.FadeIn is not null || clip.FadeOut is not null
                || !clip.Effects.IsEmpty || clip.ChannelMap is not null)
            {
                reasons.Add($"'{clip.Name}' on {track.Name} has its level, pan, fades, effects or channels changed.");
                return null;
            }

            if (clip.EffectiveSpeed != Rational.One || clip.Reverse)
            {
                reasons.Add($"'{clip.Name}' on {track.Name} plays at another speed or backwards.");
                return null;
            }

            if (!pictures.Any(picture => picture.Range == clip.Range && picture.SourceIn == clip.SourceIn))
            {
                reasons.Add($"'{clip.Name}' on {track.Name} does not line up with its picture.");
                return null;
            }
        }

        if (sound.Length != pictures.Length)
        {
            reasons.Add($"{track.Name} has gaps where the picture does not.");
            return null;
        }

        return stream;
    }

    /// <summary>In Auto, a copy only when it is also what the preset would have written.</summary>
    private static bool PresetMatches(ExportPresetInfo preset, CopySource copy, ProjectSettings settings, List<string> reasons)
    {
        (int width, int height) = ExportPresets.SizeFor(preset, settings.Width, settings.Height);

        if (!string.Equals(copy.Video.Codec, preset.Codec, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"The source picture is {copy.Video.Codec} and {preset.Name} writes {preset.Codec}, so it is encoded. Ask for --mode copy to keep the source's codec.");
            return false;
        }

        if (copy.Video.Width != width || copy.Video.Height != height)
        {
            reasons.Add($"The source picture is {copy.Video.Width}x{copy.Video.Height} and {preset.Name} writes {width}x{height}, so it is encoded.");
            return false;
        }

        if (preset.Lossless)
        {
            reasons.Add($"{preset.Name} re-encodes into its own lossless format.");
            return false;
        }

        return true;
    }

    /// <summary>Moves each stretch's ends to the nearest keyframes, recording every move.</summary>
    private static ImmutableArray<TimeRange> Snap(CopySource copy, KeyframeIndex index, List<KeyframeSnap> snaps)
    {
        Rational rate = copy.Video.FrameRate ?? Rational.Fps30;
        Flicks end = Flicks.Max(index.Duration, copy.Media.Duration);
        var result = new List<TimeRange>();

        foreach (TimeRange stretch in copy.Source)
        {
            Flicks start = Nearest(index, stretch.Start, preferLater: false, fileEnd: null);
            Flicks stop = stretch.End >= end ? end : Nearest(index, stretch.End, preferLater: true, fileEnd: end);

            if (start != stretch.Start)
            {
                snaps.Add(new KeyframeSnap("start", stretch.Start, start, start.ToFrames(rate, RoundingMode.Nearest)));
            }

            if (stop != stretch.End && stretch.End < end)
            {
                snaps.Add(new KeyframeSnap("end", stretch.End, stop, stop.ToFrames(rate, RoundingMode.Nearest)));
            }

            if (stop <= start)
            {
                continue;
            }

            if (result.Count > 0 && start <= result[^1].End)
            {
                result[^1] = TimeRange.FromBounds(result[^1].Start, Flicks.Max(stop, result[^1].End));
            }
            else
            {
                result.Add(TimeRange.FromBounds(start, stop));
            }
        }

        return [.. result];
    }

    /// <summary>
    /// The keyframe nearest a time, within half a millisecond counting as on it. A tie goes the
    /// way that keeps more: back for a start, on for an end. The end of the file counts as a
    /// keyframe for an end.
    /// </summary>
    private static Flicks Nearest(KeyframeIndex index, Flicks time, bool preferLater, Flicks? fileEnd)
    {
        if (index.Count == 0)
        {
            return time;
        }

        // A millisecond container rounds times, so a cut within half a millisecond of a keyframe
        // is on it.
        Flicks slack = Flicks.FromMilliseconds(1) / 2;
        Flicks before = index.AtOrBefore(time);
        if ((time - before).Value <= slack.Value)
        {
            return before;
        }

        Flicks? after = index.After(time) ?? fileEnd;
        if (after is { } next && (next - time).Value <= slack.Value)
        {
            return next;
        }

        if (after is not { } later)
        {
            return before;
        }

        Flicks back = time - before;
        Flicks forward = later - time;
        return back < forward || (back == forward && !preferLater) ? before : later;
    }

    private static ExportPlan CopyPlan(
        ExportRequest request,
        ExportPresetInfo preset,
        Sequence sequence,
        string output,
        string container,
        ImmutableArray<TimeRange> ranges,
        CopySource copy,
        ImmutableArray<TimeRange> snapped,
        List<KeyframeSnap> snaps,
        List<string> reasons)
    {
        Flicks duration = Flicks.Zero;
        foreach (TimeRange stretch in snapped)
        {
            duration += stretch.Duration;
        }

        reasons.Insert(0, request.Mode == ExportMode.Copy
            ? "Copying the source's packets, as asked: no quality lost, and fast."
            : "Copying the source's packets: the timeline plays one file untouched, so nothing needs encoding.");

        if (snaps.Count > 0)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{snaps.Count} cut(s) moved to the nearest keyframe."));
        }

        return new ExportPlan(
            sequence.Id,
            preset.Name,
            ExportMode.Copy,
            output,
            container,
            duration,
            new EquatableArray<TimeRange>(ranges),
            Copy: new ExportCopy(
                copy.Media.Id,
                copy.Path,
                copy.VideoStream,
                new EquatableArray<int>(copy.AudioStreams),
                new EquatableArray<TimeRange>(snapped),
                copy.Video.IsVariableFrameRate ? null : copy.Video.FrameRate,
                new EquatableArray<string>(copy.StreamNames)),
            Reasons: [.. reasons],
            Snaps: [.. snaps],
            External: false);
    }

    private static ExportPlan EncodePlan(
        ExportRequest request,
        ExportPresetInfo preset,
        Sequence sequence,
        ProjectSettings settings,
        string output,
        string container,
        ImmutableArray<TimeRange> ranges,
        Project project,
        List<string> reasons)
    {
        (int width, int height) = ExportPresets.SizeFor(preset, settings.Width, settings.Height);
        Flicks duration = Flicks.Zero;
        foreach (TimeRange range in ranges)
        {
            duration += range.Duration;
        }

        if (request.Mode == ExportMode.Encode)
        {
            // Why a copy would not have worked is beside the point when nobody asked for one.
            reasons.Clear();
            reasons.Add("Encoding, as asked.");
        }
        else if (reasons.Count == 0)
        {
            reasons.Add("Encoding: the timeline has to be rendered.");
        }

        bool sound = sequence.Tracks.Any(track => track.IsAudio && !track.Clips.IsEmpty);

        return new ExportPlan(
            sequence.Id,
            preset.Name,
            ExportMode.Encode,
            output,
            container,
            duration,
            new EquatableArray<TimeRange>(ranges),
            Video: new ExportVideo(
                preset.Codec,
                preset.Encoders,
                width,
                height,
                settings.FrameRate,
                preset.Quality,
                0,
                preset.Speed,
                (int)Math.Max(1, Math.Round(settings.FrameRate.ToDouble() * 2)),
                preset.Lossless ? 0 : 2,
                preset.Lossless),
            Audio: sound ? new ExportAudio(preset.AudioEncoder, settings.SampleRate, settings.ChannelCount, preset.AudioBitrate) : null,
            Reasons: [.. reasons],
            External: request.External);
    }

    /// <summary>What a copy would take, before keyframes.</summary>
    private sealed record CopySource(
        MediaItem Media,
        string Path,
        int VideoStream,
        MediaStream Video,
        ImmutableArray<int> AudioStreams,
        ImmutableArray<string> StreamNames,
        ImmutableArray<TimeRange> Source);
}
