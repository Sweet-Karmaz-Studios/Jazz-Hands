using System.Collections.Immutable;
using System.Globalization;
using JazzHands.Core;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;
using JazzHands.Media.Encode;
using JazzHands.Media.SmartCut;

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
/// itself: it smart cuts instead, copying between the cuts and encoding only the frames around
/// them with an encoder matched to the source, or encodes everything when the source cannot be
/// matched, so a cut is exactly where it was put unless someone said otherwise.
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

        Sequence sequence = (request.SequenceId is { } id ? project.Sequence(id) : project.ActiveSequence)
            ?? throw new CommandException("sequence-not-found", request.SequenceId is null
                ? "The project has no sequence to export."
                : $"No sequence with id '{request.SequenceId}'.");

        // Burning subtitles in draws them into every picture, so there is nothing to copy.
        ImmutableArray<TimeRange> ranges = Ranges(sequence, request.UseInOut, request.Range);
        bool burn = request.Subtitles == SubtitleDelivery.Burn && SubtitleTracks(sequence, ranges).Length > 0;

        // Stems in the file are encoded beside the mix, so the file is encoded rather than copied.
        bool stemsInFile = request.Stems != StemMode.None && request.StemFormat == StemFormat.InFile;
        if (stemsInFile && request.Mode is ExportMode.Copy or ExportMode.Smart)
        {
            throw new CommandException("stems-need-encoding", "Stems inside the file are encoded with the mix, and this export copies. Export with --mode encode, or write the stems beside it (--stem-format wav).");
        }

        ExportPlan plan = PlanStreams(project, projectPath, stemsInFile ? request with { Mode = ExportMode.Encode } : request, keyframes, burn, cancellationToken);
        ExportContainer? container = ExportPresets.Container(plan.Container);
        var reasons = new List<string>(plan.Reasons);
        ExportSubtitles? subtitles = Subtitles(request, sequence, plan, container, reasons);
        bool chapters = request.Chapters && container is { Chapters: true };

        EquatableArray<ExportStem> stems = Stems(project, sequence, request.Stems, request.StemFormat, plan, reasons);

        // Slow motion by optical flow is drawn on the GPU by our own flow (Phase 42): say which clips.
        string[] flowing = [.. sequence.Tracks
            .Where(track => track.Kind == TrackKind.Video && !track.Muted)
            .SelectMany(track => track.Clips)
            .Where(clip => clip.Retime == RetimeMode.OpticalFlow && clip.IsMedia && (clip.IsRemapped || clip.EffectiveSpeed != Rational.One)
                && ranges.Any(range => clip.Start < range.End && clip.End > range.Start))
            .Select(clip => $"'{clip.Name}'")
            .Distinct(StringComparer.Ordinal)];
        if (flowing.Length > 0)
        {
            reasons.Add($"Slow motion by optical flow, on the GPU with Jazz Hands' own flow (no model needed): {string.Join(", ", flowing)}.");
        }

        plan = plan with
        {
            Subtitles = subtitles,
            Chapters = chapters ? Chapters(sequence, plan.Ranges, plan.Duration) : default,
            Reasons = [.. reasons],
            Stems = stems,
        };

        return plan with { Estimate = ExportEstimates.For(plan, CopiedBytes(project, plan)) };
    }

    /// <summary>
    /// Plans a Quick Trim's export: a smart cut when the source allows one, so the cuts are exact
    /// and almost nothing is encoded again, and a copy on keyframes when it does not.
    /// </summary>
    public static ExportPlan PlanSmartOrCopy(
        Project project,
        string projectPath,
        ExportRequest request,
        KeyframeLookup keyframes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return Plan(project, projectPath, request with { Mode = ExportMode.Smart }, keyframes, cancellationToken);
        }
        catch (CommandException error) when (error.Code == "cannot-smart-cut")
        {
            ExportPlan copy = Plan(project, projectPath, request with { Mode = ExportMode.Copy }, keyframes, cancellationToken);
            string why = error.Message.Replace(" Export with --mode encode instead.", string.Empty, StringComparison.Ordinal);
            return copy with { Reasons = [.. copy.Reasons, $"Copying rather than smart cutting. {why}"] };
        }
    }

    /// <summary>
    /// The stems (Phase 40): a stem per role or per sound track heard in the mix. Beside the file,
    /// each named after the file and the stem, as a 24-bit WAV or in the preset's sound codec; or in
    /// the file, as more sound tracks after the mix. A role with no sound track heard has none.
    /// </summary>
    internal static EquatableArray<ExportStem> Stems(Project project, Sequence sequence, StemMode mode, StemFormat format, ExportPlan plan, List<string> reasons)
    {
        if (mode == StemMode.None)
        {
            return default;
        }

        string outputPath = plan.OutputPath;
        if (format != StemFormat.Wav && plan.Audio is null)
        {
            throw new CommandException("no-sound", $"{plan.Preset} writes no sound, so there is no codec for the stems. Write them as WAV (--stem-format wav).");
        }

        if (format == StemFormat.InFile && (plan.Video is null || plan.Container is not ("mp4" or "mov" or "matroska")))
        {
            throw new CommandException("stems-in-file-unavailable", $"Stems as sound tracks go in an MP4, MOV or Matroska file with a picture, and {plan.Preset} writes {plan.Container}. Write them beside it (--stem-format wav or codec).");
        }

        if (format == StemFormat.InFile && plan.External)
        {
            throw new CommandException("stems-in-file-unavailable", "The ffmpeg.exe path writes the mix only. Write the stems beside the file (--stem-format wav or codec).");
        }

        Track[] heard = [.. sequence.Tracks
            .Where(track => track.Kind == TrackKind.Audio && !track.Clips.IsEmpty && Role.Heard(project, sequence, track))
            .OrderBy(track => track.Order)];
        IEnumerable<(string Name, string[] Ids)> groups = mode == StemMode.Roles
            ? heard.GroupBy(track => Role.Of(track), StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => Role.All(project).IndexOf(role => string.Equals(role.Name, group.Key, StringComparison.OrdinalIgnoreCase)) is var at && at < 0 ? int.MaxValue : at)
                .Select(group => (group.Key, group.Select(track => track.Id).ToArray()))
            : heard.Select(track => (track.Name, new[] { track.Id }));

        string folder = Path.GetDirectoryName(outputPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(outputPath);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stems = new List<ExportStem>();
        foreach ((string name, string[] ids) in groups)
        {
            string safe = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
            string file = $"{stem} - {safe}";
            for (int copy = 2; !used.Add(file); copy++)
            {
                file = $"{stem} - {safe} {copy}";
            }

            (string Container, string Extension) sound = format == StemFormat.Wav ? ("wav", ".wav") : ExportOverrideText.SoundFile(plan.Audio!.Encoder);
            stems.Add(format switch
            {
                StemFormat.InFile => new ExportStem(name, outputPath, [.. ids], plan.Audio!.Encoder, plan.Container, plan.Audio.Bitrate, InFile: true),
                StemFormat.Codec => new ExportStem(name, Path.Combine(folder, file + sound.Extension), [.. ids], plan.Audio!.Encoder, sound.Container, plan.Audio.Bitrate),
                _ => new ExportStem(name, Path.Combine(folder, file + ".wav"), [.. ids]),
            });
        }

        if (stems.Count > 0)
        {
            string names = string.Join(", ", stems.Select(item => item.Name));
            string count = $"{stems.Count} {(stems.Count == 1 ? "stem" : "stems")}";
            reasons.Add(format switch
            {
                StemFormat.InFile => $"Writes {count} as sound tracks in the file after the mix, in {plan.Audio!.Encoder}: {names}.",
                StemFormat.Codec => $"Writes {count} beside it, in {plan.Audio!.Encoder}: {names}.",
                _ => $"Writes {count} beside it, 24-bit WAV: {names}.",
            });
        }

        return [.. stems];
    }

    /// <summary>For a copy or a smart cut, about how many of the source's bytes it takes: its share of the file by time.</summary>
    private static long CopiedBytes(Project project, ExportPlan plan)
    {
        (string? mediaId, EquatableArray<TimeRange> ranges) = plan.Copy is { } copy ? (copy.MediaId, copy.SourceRanges)
            : plan.Smart is { } smart ? (smart.MediaId, smart.SourceRanges)
            : (null, default);
        if (mediaId is null || project.MediaItem(mediaId) is not { Info: { } info } media || media.Duration <= Flicks.Zero)
        {
            return 0;
        }

        double kept = ranges.Sum(range => range.Duration.ToSeconds());
        return (long)(info.SizeBytes * Math.Min(1.0, kept / media.Duration.ToSeconds()));
    }

    /// <summary>The subtitle tracks an export of these ranges carries: not muted, with a cue inside.</summary>
    public static ImmutableArray<Track> SubtitleTracks(Sequence sequence, IReadOnlyList<TimeRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(ranges);
        return [.. sequence.Tracks
            .Where(track => track.Kind == TrackKind.Subtitle && !track.Muted)
            .Where(track => track.Clips.Any(clip => clip.Cue is not null && clip.Enabled && ranges.Any(range => clip.Start < range.End && clip.End > range.Start)))
            .OrderBy(track => track.Order)];
    }

    /// <summary>
    /// Where each chapter falls in the output: moved by the stretches before it, a chapter in a
    /// gap starting where the next stretch does, and one past the end left out.
    /// </summary>
    public static ImmutableArray<ExportChapter> Chapters(Sequence sequence, IReadOnlyList<TimeRange> ranges, Flicks duration)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(ranges);

        Flicks? Map(Flicks time)
        {
            Flicks before = Flicks.Zero;
            foreach (TimeRange range in ranges)
            {
                if (time < range.End)
                {
                    return before + (time > range.Start ? time - range.Start : Flicks.Zero);
                }

                before += range.Duration;
            }

            return null;
        }

        (Flicks Start, string Title)[] starts = [.. sequence.Markers
            .Where(marker => marker.IsChapter)
            .OrderBy(marker => marker.Time)
            .Select(marker => (Start: Map(marker.Time), marker.Name))
            .Where(mapped => mapped.Start is { } start && start < duration)
            .Select(mapped => (mapped.Start!.Value, mapped.Name))
            .GroupBy(mapped => mapped.Value)
            .Select(group => group.Last())];

        return [.. starts.Select((chapter, index) => new ExportChapter(
            chapter.Start,
            index + 1 < starts.Length ? starts[index + 1].Start : duration,
            chapter.Title))];
    }

    /// <summary>How the plan carries the subtitle tracks, or null when there are none to carry.</summary>
    private static ExportSubtitles? Subtitles(ExportRequest request, Sequence sequence, ExportPlan plan, ExportContainer? container, List<string> reasons)
    {
        ImmutableArray<Track> tracks = SubtitleTracks(sequence, plan.Ranges);
        if (tracks.IsEmpty || request.Subtitles == SubtitleDelivery.None)
        {
            return null;
        }

        if (request.Subtitles == SubtitleDelivery.Burn && plan.Video is null && plan.Mode == ExportMode.Encode)
        {
            reasons.Add("There is no picture to burn the subtitles into, so they are left out; export them beside it with --subtitles sidecar.");
            return null;
        }

        // ffmpeg.exe is handed the picture and the sound; subtitles go beside its file.
        SubtitleDelivery delivery = request.Subtitles == SubtitleDelivery.Soft && plan.External ? SubtitleDelivery.Sidecar : request.Subtitles;
        if (delivery == SubtitleDelivery.Soft && container?.Subtitles is null)
        {
            reasons.Add($"{plan.Container} files carry no subtitle streams, so the subtitle tracks are left out; export them beside it with --subtitles sidecar, or burn them in.");
            return null;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new ExportSubtitles(
            delivery,
            [.. tracks.Select((track, index) => new ExportSubtitleTrack(
                track.Id,
                track.Name,
                track.Language,
                delivery == SubtitleDelivery.Soft ? Codec(container!, track) : null,
                delivery == SubtitleDelivery.Sidecar ? SidecarPath(plan.OutputPath, track, request.SidecarFormat, used) : null,
                Default: index == 0))],
            request.SidecarFormat);
    }

    /// <summary>
    /// The subtitle encoder for a container: mov_text is all MP4 carries and WebVTT all WebM does;
    /// Matroska takes ASS when the track's look or a cue's place needs it, and SubRip, which
    /// everything plays, when not.
    /// </summary>
    private static string Codec(ExportContainer container, Track track) =>
        container.Muxer != "matroska"
            ? container.Subtitles!
            : track.SubtitleStyle is not null || track.Clips.Any(clip => clip.Cue is { Align: not SubtitleAlign.Bottom })
                ? "ass"
                : "subrip";

    /// <summary>A file beside the video for a track: <c>trailer.eng.srt</c>, or <c>trailer.srt</c> without a language.</summary>
    private static string SidecarPath(string output, Track track, Core.Subtitles.SubtitleFormat format, HashSet<string> used)
    {
        string stem = Path.Combine(Path.GetDirectoryName(output) ?? ".", Path.GetFileNameWithoutExtension(output));
        string extension = Core.Subtitles.SubtitleFiles.Extension(format);
        string name = track.Language is { } language ? $"{stem}.{language}" : stem;
        string path = name + extension;

        for (int copy = 2; !used.Add(path); copy++)
        {
            path = $"{name}.{copy}{extension}";
        }

        return path;
    }

    private static ExportPlan PlanStreams(
        Project project,
        string projectPath,
        ExportRequest request,
        KeyframeLookup keyframes,
        bool burn,
        CancellationToken cancellationToken)
    {
        ExportPreset preset = ExportOverrideText.Apply(ExportPresetLibrary.Require(request.Preset), request.Overrides);

        Sequence sequence = (request.SequenceId is { } id ? project.Sequence(id) : project.ActiveSequence)
            ?? throw new CommandException("sequence-not-found", request.SequenceId is null
                ? "The project has no sequence to export."
                : $"No sequence with id '{request.SequenceId}'.");

        ProjectSettings settings = project.SettingsFor(sequence);
        (string output, string container) = Output(request.OutputPath, projectPath, preset);
        ImmutableArray<TimeRange> ranges = Ranges(sequence, request.UseInOut, request.Range);

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

        if (burn && copy is not null)
        {
            reasons.Add("The subtitles are burned into the picture, so every frame is drawn.");
            copy = null;
        }

        if (copy is not null && ExportPresets.Container(container) is { } carrier && !carrier.VideoCodecs.Contains(CodecOf(copy.Video.Codec)))
        {
            reasons.Add(carrier.VideoCodecs.IsEmpty || carrier.Sequence
                ? $"{Path.GetExtension(output)} files do not hold a video stream to copy into."
                : $"{Path.GetExtension(output)} files cannot carry the source's {copy.Video.Codec} picture.");
            copy = null;
        }

        if (request.Mode == ExportMode.Copy && copy is null)
        {
            throw new CommandException(
                "cannot-copy",
                $"This cannot be exported by stream copy: {string.Join(" ", reasons)} Export with --mode encode instead.");
        }

        if (request.Mode == ExportMode.Smart && copy is null)
        {
            throw new CommandException(
                "cannot-smart-cut",
                $"This cannot be smart cut: {string.Join(" ", reasons)} Export with --mode encode instead.");
        }

        if (copy is not null && request.Mode != ExportMode.Encode)
        {
            if (request.Mode == ExportMode.Auto && !PresetMatches(preset, copy, settings, sequence.QuickTrim is not null, reasons))
            {
                copy = null;
            }
        }
        else
        {
            copy = null;
        }

        if (copy is not null && request.Mode == ExportMode.Smart)
        {
            // Cuts are exact, so nothing snaps; a smart cut that only copies is a copy.
            KeyframeIndex index = keyframes.Get(copy.Media, copy.Path, copy.VideoStream, cancellationToken);
            return SmartPlan(request, preset, sequence, output, container, ranges, copy, index, reasons)
                ?? throw new CommandException(
                    "cannot-smart-cut",
                    $"This cannot be smart cut: {string.Join(" ", reasons)} Export with --mode encode instead.");
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
                        $"{where} A stream copy can only cut on keyframes. Let the cuts move there (--snap-to-keyframes; jazz trim does unless --exact), or export with --mode encode to cut exactly.");
                }

                reasons.Add(where);
                if (SmartPlan(request, preset, sequence, output, container, ranges, copy, index, reasons) is { } smart)
                {
                    return smart;
                }

                reasons.Add("Encoding so every cut lands exactly where it was put; ask for a copy with snapping to keep the source untouched.");
                copy = null;
            }
            else if (snapped.IsEmpty)
            {
                throw new CommandException(
                    "nothing-to-export",
                    "Every stretch is shorter than the distance between two keyframes, so a copy would keep nothing. Export with --mode encode.");
            }
            else if (index.HasLeadingPictures && EndsEarly(copy, index, snapped))
            {
                // An open group of pictures: the pictures shown just before a keyframe are decoded
                // after it and refer to it, so a stretch that stops at that keyframe cannot keep
                // them. A smart cut encodes them again.
                const string OpenGop =
                    "The source uses open groups of pictures, so each stretch that ends at a keyframe loses the few pictures shown just before it, which depend on that keyframe.";

                if (request.Mode == ExportMode.Copy)
                {
                    reasons.Add($"{OpenGop} Smart cut (--mode smart) keeps them.");
                    return CopyPlan(request, preset, sequence, output, container, ranges, copy, snapped, snaps, reasons);
                }

                reasons.Add(OpenGop);
                if (SmartPlan(request, preset, sequence, output, container, ranges, copy, index, reasons) is { } smart)
                {
                    return smart;
                }

                reasons.Add("Encoding instead, so no picture is lost.");
                copy = null;
            }
            else
            {
                return CopyPlan(request, preset, sequence, output, container, ranges, copy, snapped, snaps, reasons);
            }
        }

        return EncodePlan(request, preset, sequence, settings, output, container, ranges, project, reasons);
    }

    /// <summary>The stretches of the sequence an export plays, back to back, in sequence time.</summary>
    /// <param name="sequence">The sequence.</param>
    /// <param name="useInOut">Only between its in and out points.</param>
    /// <param name="range">Only inside this stretch too, or null.</param>
    public static ImmutableArray<TimeRange> Ranges(Sequence sequence, bool useInOut, TimeRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        ImmutableArray<TimeRange> whole = sequence.QuickTrim is not null
            ? QuickTrimOps.Segments(sequence)
            : sequence.Duration > Flicks.Zero ? [new TimeRange(Flicks.Zero, sequence.Duration)] : [];

        if (useInOut)
        {
            TimeRange inOut = sequence.InOut
                ?? throw new CommandException("no-in-out", $"'{sequence.Name}' has no in and out points to export between.");
            whole = Limit(whole, inOut);
        }

        return range is { } only ? Limit(whole, only) : whole;
    }

    private static ImmutableArray<TimeRange> Limit(ImmutableArray<TimeRange> ranges, TimeRange limit) =>
        [.. ranges.Where(range => range.Intersects(limit)).Select(range => range.Intersect(limit)).Where(range => !range.IsEmpty)];

    private static (string Path, string Container) Output(string requested, string projectPath, ExportPreset preset)
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

        string extension = Path.GetExtension(path);
        ExportContainer container = ExportPresets.ContainerForExtension(extension)
            ?? throw new CommandException(
                "unsupported-container",
                $"Jazz Hands does not write '{extension}' files. It writes {string.Join(", ", ExportPresets.Containers.SelectMany(c => c.Extensions))}.");

        if (preset.Video is { } video && !container.VideoCodecs.Contains(video.Codec))
        {
            throw new CommandException(
                "unsupported-container",
                container.VideoCodecs.IsEmpty
                    ? $"{preset.Name} writes a picture, and {extension} files hold only sound. Use {preset.Extension}."
                    : $"{preset.Name} writes {ExportPresets.CodecName(video.Codec)}, which {extension} files cannot carry. Use {preset.Extension}.");
        }

        if (preset.Video is null && container.AudioEncoders.IsEmpty)
        {
            throw new CommandException(
                "unsupported-container",
                $"{preset.Name} writes only sound, and {extension} files hold pictures. Use {preset.Extension}.");
        }

        if (preset.Audio is { } audio && !container.AudioEncoders.IsEmpty && !container.AudioEncoders.Contains(audio.Encoder))
        {
            throw new CommandException(
                "unsupported-container",
                $"{preset.Name} writes {audio.Encoder} sound, which {extension} files cannot carry. Use {preset.Extension}, or pick the sound with --audio-encoder.");
        }

        if (container.Sequence && !path.Contains('%', StringComparison.Ordinal))
        {
            // One file a frame, numbered from 1: shots.png is written as shots_00001.png onwards.
            path = Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path) + "_%05d" + extension);
        }

        if (Directory.Exists(path))
        {
            throw new CommandException("output-is-folder", $"'{path}' is a folder. Name a file.");
        }

        return (path, container.Muxer);
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

        // An ACES project's pictures all go through its output transform, which a copy would skip.
        if (project.Settings.ColorManagement is { IsAces: true })
        {
            reasons.Add("The project is colour managed in ACES, so every picture goes through its output transform.");
            return null;
        }

        bool anySolo = sequence.Tracks.Any(track => track.Solo);
        bool Audible(Track track) => !track.Muted && (!anySolo || track.Solo || !track.IsAudio);

        Track[] pictures = [.. sequence.Tracks.Where(track =>
            track.Kind is TrackKind.Video or TrackKind.Adjustment && !track.Muted && Within(track, ranges).Any())];

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

        if (!picture.Transitions.IsEmpty)
        {
            reasons.Add($"{picture.Name} has transitions, which are drawn.");
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

            if (!track.Transitions.IsEmpty)
            {
                reasons.Add($"{track.Name} has crossfades, which are mixed.");
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
    private static bool PresetMatches(ExportPreset preset, CopySource copy, ProjectSettings settings, bool quickTrim, List<string> reasons)
    {
        if (preset.Video is not { } video)
        {
            reasons.Add($"{preset.Name} writes only sound.");
            return false;
        }

        (int width, int height) = ExportPresets.SizeFor(video, settings.Width, settings.Height);

        if (!string.Equals(CodecOf(copy.Video.Codec), video.Codec, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"The source picture is {copy.Video.Codec} and {preset.Name} writes {video.Codec}, so it is encoded. Ask for --mode copy to keep the source's codec.");
            return false;
        }

        if (copy.Video.Width != width || copy.Video.Height != height)
        {
            reasons.Add($"The source picture is {copy.Video.Width}x{copy.Video.Height} and {preset.Name} writes {width}x{height}, so it is encoded.");
            return false;
        }

        if (ExportPresets.FrameRateFor(video, settings.FrameRate) != settings.FrameRate)
        {
            reasons.Add($"{preset.Name} writes at most {video.MaxFrameRate} fps, slower than the sequence, so it is encoded.");
            return false;
        }

        if (video.Lossless)
        {
            reasons.Add($"{preset.Name} re-encodes into its own lossless format.");
            return false;
        }

        if (preset.TargetBytes > 0)
        {
            reasons.Add($"{preset.Name} has to come in under {ExportPresets.FormatBytes(preset.TargetBytes)}, so it is encoded to fit.");
            return false;
        }

        if (preset.Loudness is not null)
        {
            reasons.Add("The sound is normalised, so it is encoded.");
            return false;
        }

        // The sound as well, on a timeline: a preset writes one mix in its own codec. A copy of an
        // OBS recording would keep the game, the microphone and the chat as separate streams, and
        // YouTube and most players play only the first. A Quick Trim keeps the recording's streams
        // as they are, which is what it is for.
        if (quickTrim || copy.AudioStreams.IsEmpty)
        {
            return true;
        }

        if (preset.Audio is not { } audio)
        {
            reasons.Add($"{preset.Name} writes no sound, so it is encoded.");
            return false;
        }

        if (copy.AudioStreams.Length > 1)
        {
            reasons.Add($"The source's {copy.AudioStreams.Length} sound streams ({string.Join(", ", copy.StreamNames)}) are mixed into one, as {preset.Name} writes, so it is encoded. Ask for --mode copy to keep them apart.");
            return false;
        }

        string wanted = SoundCodecOf(audio.Encoder);
        if (copy.Media.Info?.Streams.FirstOrDefault(stream => stream.Index == copy.AudioStreams[0]) is { } sound
            && !string.Equals(sound.Codec, wanted, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"The source sound is {sound.Codec} and {preset.Name} writes {wanted}, so it is encoded. Ask for --mode copy to keep the source's sound.");
            return false;
        }

        return true;
    }

    /// <summary>The codec a preset's sound encoder writes, as a probe names it.</summary>
    private static string SoundCodecOf(string encoder) => encoder.ToLowerInvariant() switch
    {
        "libopus" => "opus",
        "libmp3lame" => "mp3",
        string other => other,
    };

    /// <summary>The preset codec a source codec is: FFmpeg calls DNxHR dnxhd.</summary>
    private static string CodecOf(string source) => source.ToLowerInvariant() switch
    {
        "dnxhd" => "dnxhr",
        string other => other,
    };

    /// <summary>True when some stretch stops at a keyframe rather than running to the end of the file.</summary>
    private static bool EndsEarly(CopySource copy, KeyframeIndex index, ImmutableArray<TimeRange> snapped)
    {
        Rational rate = copy.Video.FrameRate ?? Rational.Fps30;
        Flicks endish = Flicks.Min(index.Duration, copy.Media.Duration) - (Flicks.FromFrames(1, rate) / 2);
        return snapped.Any(stretch => stretch.End < endish);
    }

    /// <summary>Moves each stretch's ends to the nearest keyframes, recording every move.</summary>
    private static ImmutableArray<TimeRange> Snap(CopySource copy, KeyframeIndex index, List<KeyframeSnap> snaps)
    {
        Rational rate = copy.Video.FrameRate ?? Rational.Fps30;

        // The probe and the packet scan disagree about where a file ends by a rounding or two, so
        // anything within half a frame of the earlier of them is the end. No keyframe follows it,
        // so the copier takes every packet there is whatever the stretch says.
        Flicks end = Flicks.Min(index.Duration, copy.Media.Duration);
        Flicks endish = end - (Flicks.FromFrames(1, rate) / 2);
        var result = new List<TimeRange>();

        foreach (TimeRange stretch in copy.Source)
        {
            bool toTheEnd = stretch.End >= endish;
            Flicks start = Nearest(index, stretch.Start, preferLater: false, fileEnd: null);
            Flicks stop = toTheEnd ? stretch.End : Nearest(index, stretch.End, preferLater: true, fileEnd: end);

            if (start != stretch.Start)
            {
                snaps.Add(new KeyframeSnap("start", stretch.Start, start, start.ToFrames(rate, RoundingMode.Nearest)));
            }

            if (stop != stretch.End && !toTheEnd)
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

    /// <summary>
    /// A smart cut of a file a copy could take: exact cuts, the groups of pictures between them
    /// copied and the frames around each cut encoded again with an encoder matched to the source.
    /// Null, with the reason added, when the source cannot be smart cut.
    /// </summary>
    private static ExportPlan? SmartPlan(
        ExportRequest request,
        ExportPreset preset,
        Sequence sequence,
        string output,
        string container,
        ImmutableArray<TimeRange> ranges,
        CopySource copy,
        KeyframeIndex index,
        List<string> reasons)
    {
        if (copy.Video.IsVariableFrameRate || copy.Video.FrameRate is not { } rate)
        {
            reasons.Add($"'{copy.Media.Name}' has no constant frame rate, and a smart cut joins its frames on one.");
            return null;
        }

        int gop = SmartSegments.GopFrames(index, rate);
        // --encoder chooses the matched encoder too: libx264 keeps a smart cut off a busy GPU.
        IReadOnlyList<string> asked = request.Overrides?.Encoders is { IsEmpty: false } named ? [.. named] : [];
        SmartCutChecks.Answer answer = SmartCutChecks.For(copy.Path, copy.VideoStream, copy.AudioStreams, gop, asked);
        if (answer.Source is null)
        {
            reasons.Add(answer.Reason ?? "The source cannot be smart cut.");
            return null;
        }

        Flicks fileEnd = Flicks.Min(index.Duration, copy.Media.Duration);
        IReadOnlyList<SmartSegment> pieces = SmartSegments.For(index, copy.Source, rate, fileEnd);
        if (pieces.Count == 0)
        {
            throw new CommandException("nothing-to-export", "Every stretch is shorter than a frame, so there is nothing to export.");
        }

        if (pieces.All(piece => !piece.Encode))
        {
            // Every cut is on a keyframe and loses nothing: a copy is the same file, without
            // opening an encoder.
            reasons.Insert(0, "Every cut is on a keyframe, so nothing needs encoding again: copying the source's packets.");
            return CopyPlan(
                request,
                preset,
                sequence,
                output,
                container,
                ranges,
                copy,
                [.. pieces.Select(piece => TimeRange.FromBounds(piece.Start, piece.End))],
                [],
                reasons);
        }

        var smart = new ExportSmart(
            copy.Media.Id,
            copy.Path,
            copy.VideoStream,
            new EquatableArray<int>(copy.AudioStreams),
            new EquatableArray<TimeRange>(copy.Source),
            [.. pieces.Select(piece => new ExportSegment(piece.Start, piece.End, piece.Encode, piece.From))],
            rate,
            new EquatableArray<string>(answer.Encoders),
            gop,
            new EquatableArray<string>(copy.StreamNames));

        Flicks duration = Flicks.Zero;
        foreach (SmartSegment piece in pieces)
        {
            duration += piece.Duration;
        }

        int encoded = pieces.Count(piece => piece.Encode);
        reasons.Insert(0, string.Create(
            CultureInfo.InvariantCulture,
            $"Smart cut: {Words.Count(encoded, "piece")} around the cuts, {Words.Count(smart.EncodedFrames, "frame")}, encoded again with {answer.Encoders[0]}; {Words.Count(smart.CopiedFrames, "frame")} more copied as the source's own packets."));

        if (answer.Skipped is { } skipped)
        {
            reasons.Add(skipped);
        }

        if (!copy.AudioStreams.IsEmpty)
        {
            reasons.Add("The sound is cut to the sample: PCM in its packets, anything else encoded again with its own codec.");
        }

        return new ExportPlan(
            sequence.Id,
            preset.Name,
            ExportMode.Smart,
            output,
            container,
            duration,
            new EquatableArray<TimeRange>(ranges),
            Reasons: [.. reasons],
            External: false,
            Smart: smart);
    }

    private static ExportPlan CopyPlan(
        ExportRequest request,
        ExportPreset preset,
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

        if (request.Mode != ExportMode.Smart)
        {
            reasons.Insert(0, request.Mode == ExportMode.Copy
                ? "Copying the source's packets, as asked: no quality lost, and fast."
                : "Copying the source's packets: the timeline plays one file untouched, so nothing needs encoding.");
        }

        if (snaps.Count > 0)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Words.Count(snaps.Count, "cut")} moved to the nearest keyframe."));
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
        ExportPreset preset,
        Sequence sequence,
        ProjectSettings settings,
        string output,
        string container,
        ImmutableArray<TimeRange> ranges,
        Project project,
        List<string> reasons)
    {
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

        bool sound = preset.Audio is not null && sequence.Tracks.Any(track => track.IsAudio && !track.Clips.IsEmpty);
        if (preset.Video is null && !sound)
        {
            throw new CommandException("nothing-to-export", $"{preset.Name} writes only sound, and '{sequence.Name}' has none.");
        }

        ExportAudio? audio = sound ? Audio(preset, preset.Audio!, settings, reasons) : null;
        ExportVideo? video = preset.Video is { } picture ? Video(request, picture, settings, reasons) : null;

        // An ACES project rendered for HDR10 (Phase 44) is written as HDR10: nothing else holds the
        // PQ picture its output transform renders.
        if (video is not null && project.Settings.ColorManagement is { IsAces: true, Output: AcesOutput.Hdr10 })
        {
            video = Hdr10(preset, video, request, reasons);
        }

        if (preset.TargetBytes > 0 && video is not null)
        {
            video = SizeTarget(preset, video, audio, settings, duration, reasons);
        }

        return new ExportPlan(
            sequence.Id,
            preset.Name,
            ExportMode.Encode,
            output,
            container,
            duration,
            new EquatableArray<TimeRange>(ranges),
            Video: video,
            Audio: audio,
            Reasons: [.. reasons],
            External: request.External && video is not null,
            TargetBytes: video is null ? 0 : preset.TargetBytes);
    }

    /// <summary>The picture a preset writes for a sequence: its size, rate, keyframes and encoder settings.</summary>
    private static ExportVideo Video(ExportRequest request, ExportPresetVideo video, ProjectSettings settings, List<string> reasons)
    {
        (int width, int height) = ExportPresets.SizeFor(video, settings.Width, settings.Height);

        // An asked-for rate is the rate; a preset's is a ceiling, so a 30 fps sequence is not
        // written at 60 by a preset that allows 60.
        Rational rate = request.Overrides?.FrameRate ?? ExportPresets.FrameRateFor(video, settings.FrameRate);
        if (rate != settings.FrameRate)
        {
            double ratio = settings.FrameRate.ToDouble() / rate.ToDouble();
            string how = Math.Abs(ratio - Math.Round(ratio)) < 1e-6 && ratio > 1
                ? $"every {Ordinal((int)Math.Round(ratio))} frame."
                : "each frame is the sequence's frame at that moment, so some are repeated or dropped.";
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Written at {rate.ToDouble():0.###} fps from a {settings.FrameRate.ToDouble():0.###} fps sequence: {how}"));
        }

        bool intra = video.KeyframeSeconds <= 0;
        int gop = intra ? 1 : (int)Math.Max(1, Math.Round(rate.ToDouble() * video.KeyframeSeconds));
        bool reorders = !video.Lossless && !intra && video.Codec is "h264" or "hevc" or "av1" or "vp9";

        return new ExportVideo(
            video.Codec,
            ExportPresets.EncodersFor(video),
            width,
            height,
            rate,
            video.Quality,
            video.Bitrate,
            video.Speed,
            gop,
            reorders ? 2 : 0,
            video.Lossless,
            video.PixelFormat,
            video.Profile,
            video.Level);
    }

    /// <summary>
    /// The picture of an ACES project rendered for HDR10: ten bits (HEVC's Main 10 profile), BT.2020
    /// with the PQ curve, and the mastering display and light levels the output transform renders
    /// for. HEVC and AV1 carry it; H.264 and the rest are refused rather than written wrong.
    /// </summary>
    private static ExportVideo Hdr10(ExportPreset preset, ExportVideo video, ExportRequest request, List<string> reasons)
    {
        if (video.Codec is not ("hevc" or "av1"))
        {
            throw new CommandException(
                "hdr-needs-hevc-or-av1",
                $"This ACES project is rendered for HDR10, which {preset.Name} ({video.Codec}) cannot carry. Export with an HEVC or AV1 preset (youtube-4k, youtube-1440p, youtube-4k-av1, shield-direct), or set the output to Rec.709: jazz project set-color-management --output rec709.");
        }

        if (request.External)
        {
            throw new CommandException(
                "hdr-external-unavailable",
                "The ffmpeg.exe path writes BT.709 only. Export HDR10 without --use-external-ffmpeg.");
        }

        string format = video.TenBit ? video.PixelFormat! : "yuv420p10le";
        string? profile = video.Codec == "hevc" && video.Profile is null or "main" ? "main10" : video.Profile;
        reasons.Add("HDR10: ten bit BT.2020 with the PQ curve, and a 1000 nit P3-D65 mastering display and a brightest pixel of 1000 nits written into the stream and the file.");
        return video with { PixelFormat = format, Profile = profile, Hdr10 = true };
    }

    /// <summary>The sound a preset writes for a sequence: the encoder's rate and channel count, folded down where it must be.</summary>
    private static ExportAudio Audio(ExportPreset preset, ExportPresetAudio audio, ProjectSettings settings, List<string> reasons)
    {
        int channels = audio.Channels > 0 ? audio.Channels : settings.ChannelCount;
        if (channels > 2 && audio.Encoder == "libmp3lame")
        {
            reasons.Add("MP3 carries at most two channels, so the 5.1 mix is folded down to stereo.");
            channels = 2;
        }
        else if (channels < settings.ChannelCount)
        {
            reasons.Add($"{preset.Name} writes {Channels(channels)}, so the {Channels(settings.ChannelCount)} mix is folded down.");
        }

        // Opus runs at 48 kHz and nothing else; AC-3 and E-AC-3 at 32, 44.1 or 48.
        int rate = audio.SampleRate > 0 ? audio.SampleRate : settings.SampleRate;
        if (audio.Encoder == "libopus" && rate != 48_000)
        {
            rate = 48_000;
        }
        else if (audio.Encoder is "ac3" or "eac3" && rate is not (32_000 or 44_100 or 48_000))
        {
            rate = 48_000;
        }

        return new ExportAudio(audio.Encoder, rate, channels, audio.Bitrate, preset.Loudness);
    }

    /// <summary>
    /// Picks the bitrate, and when it must a smaller picture, that brings a file in under the
    /// preset's size: the budget less three percent for the container, less the sound, over the
    /// length. A picture needs about 0.035 bits a pixel a frame to look like anything in H.264;
    /// below that the next size down looks better than this one starved.
    /// </summary>
    private static ExportVideo SizeTarget(ExportPreset preset, ExportVideo video, ExportAudio? audio, ProjectSettings settings, Flicks duration, List<string> reasons)
    {
        double seconds = Math.Max(0.04, duration.ToSeconds());
        long soundBits = audio is null ? 0 : audio.Bitrate > 0 ? audio.Bitrate : 192_000;
        double budget = (preset.TargetBytes * 8.0 * 0.97 / seconds) - soundBits;
        if (budget < 50_000)
        {
            throw new CommandException(
                "size-target-too-small",
                string.Create(CultureInfo.InvariantCulture, $"{ExportPresets.FormatBytes(preset.TargetBytes)} is too small for {seconds:0.#} s: the sound alone takes most of it. Export a shorter stretch, or pick a bigger target."));
        }

        double rate = video.FrameRate.ToDouble();
        double Needs(int width, int height) => width * (double)height * rate * 0.035;

        (int width, int height) = (video.Width, video.Height);
        foreach (int step in new[] { 1080, 720, 540, 480, 360, 240 }.Where(step => step < video.Height))
        {
            if (budget >= Needs(width, height))
            {
                break;
            }

            (width, height) = ExportPresets.Fit(settings.Width, settings.Height, 0, step);
        }

        var picture = new System.Text.StringBuilder(string.Create(
            CultureInfo.InvariantCulture,
            $"Aiming under {ExportPresets.FormatBytes(preset.TargetBytes)} for {seconds:0.#} s: {budget / 1000:0} kb/s of picture at {width}x{height}"));
        picture.Append(soundBits > 0 ? string.Create(CultureInfo.InvariantCulture, $", after {soundBits / 1000} kb/s of sound.") : ".");
        if (height < video.Height)
        {
            picture.Append(string.Create(CultureInfo.InvariantCulture, $" Smaller than {video.Width}x{video.Height}, which needs about {Needs(video.Width, video.Height) / 1000:0} kb/s to look right."));
        }

        if (budget < Needs(width, height))
        {
            picture.Append(" Even so it will look rough: a shorter stretch would look better.");
        }

        picture.Append(" The file is checked when it is done and encoded again, smaller, if it is over.");
        reasons.Add(picture.ToString());
        return video with { Width = width, Height = height, Bitrate = (long)budget };
    }

    private static string Channels(int count) => count switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        _ => string.Create(CultureInfo.InvariantCulture, $"{count} channel"),
    };

    private static string Ordinal(int value) => value switch
    {
        2 => "second",
        3 => "third",
        4 => "fourth",
        _ => string.Create(CultureInfo.InvariantCulture, $"{value}th"),
    };

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
