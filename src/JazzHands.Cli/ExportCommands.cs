using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Time;
using JazzHands.Engine;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Export;
using JazzHands.Media.Interop;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Cli;

/// <summary>
/// <c>jazz trim</c> and <c>jazz export</c>: exports that run in the foreground and print as they go.
/// </summary>
/// <remarks>
/// Both go the long way round on purpose. <c>jazz trim</c> imports the file, starts a Quick Trim,
/// keeps the stretches and mutes the lanes with the same commands the GUI sends, then plans and
/// exports with the same planner and exporter the queue runs. That is what makes a trim typed
/// here and the same trim made with the mouse the same file, byte for byte.
///
/// The queue's commands (<c>jazz export enqueue</c> and the rest) belong to a running editor; a
/// headless process has nowhere to keep a queue, so these two export in the foreground instead.
/// </remarks>
public static class ExportCommands
{
    /// <summary>Adds the verbs to the root command.</summary>
    public static void AddTo(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);

        root.Subcommands.Add(BuildTrim());
        root.Subcommands.Add(BuildExport());
    }

    private static Command BuildTrim()
    {
        var file = new Argument<string>("file") { Description = "The recording to trim." };
        var keep = new Option<string?>("--keep")
        {
            Description = "The stretches to keep, in source time: 00:10-00:25,01:00-01:30. All of it when left out.",
        };
        var mute = new Option<string?>("--mute-stream")
        {
            Description = "Sound streams to leave out, by container index or title: 2, or Mic, or 2,3.",
        };
        var output = new Option<string>("--out")
        {
            Description = "Where to write the file. The extension picks the container: .mp4, .mkv or .mov.",
            Required = true,
        };
        var mode = new Option<string>("--mode")
        {
            Description = "copy (lossless, cuts on keyframes) or encode (exact cuts, re-encoded).",
            DefaultValueFactory = _ => "copy",
        };
        var exact = new Option<bool>("--exact")
        {
            Description = "For a copy, refuse cuts that are not on keyframes instead of moving them to the nearest.",
        };
        var preset = new Option<string>("--preset")
        {
            Description = "The preset for an encode.",
            DefaultValueFactory = _ => ExportPresets.Default,
        };
        var external = new Option<bool>("--use-external-ffmpeg")
        {
            Description = "Encode through ffmpeg.exe, to tell an encoder problem from ours.",
        };
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Print the plan and write nothing.",
        };
        var save = new Option<string?>("--save")
        {
            Description = "Also save the Quick Trim as a project, to open in the editor.",
        };

        var command = new Command("trim", "Cut stretches out of one recording and write them back to back, losslessly by default.")
        {
            file, keep, mute, output, mode, exact, preset, external, dryRun, save,
        };

        command.SetAction(parse => Guard(parse, token =>
        {
            ExportMode chosen = ParseMode(parse.GetValue(mode)!, allowAuto: false);
            string source = Path.GetFullPath(parse.GetValue(file)!);
            if (!File.Exists(source))
            {
                throw new CommandException("file-not-found", $"'{source}' does not exist.");
            }

            string? projectPath = parse.GetValue(save) is { } saveAs ? Path.GetFullPath(EnsureExtension(saveAs)) : null;

            using ServiceProvider services = new ServiceCollection().AddJazzHandsEngine().BuildServiceProvider();
            Project project = Project.CreateNew(Path.GetFileNameWithoutExtension(source));
            Session session = new(project, services, projectPath ?? string.Empty);

            try
            {
                Run(session, new AddMediaCommand([source]), token);
                MediaItem media = session.Project.Media.Single();
                Run(session, new StartTrimCommand(media.Id), token);

                Rational rate = session.Project.SettingsFor(session.Project.ActiveSequence!).FrameRate;
                if (parse.GetValue(keep) is { Length: > 0 } ranges)
                {
                    var keepRanges = (EquatableArray<TimeRange>)CommandValues.Parse(typeof(EquatableArray<TimeRange>), ranges, rate, "keep")!;
                    Run(session, new SetTrimSegmentsCommand(keepRanges), token);
                }

                foreach (string stream in Split(parse.GetValue(mute)))
                {
                    Run(session, new SetTrackMuteCommand(LaneFor(session.Project, media, stream), true), token);
                }

                if (projectPath is not null)
                {
                    ProjectFile.Save(projectPath, session.Project);
                }

                var request = new ExportRequest(
                    parse.GetValue(output)!,
                    parse.GetValue(preset)!,
                    chosen,
                    SnapToKeyframes: !parse.GetValue(exact),
                    External: parse.GetValue(external));

                return Export(session.Project, projectPath ?? string.Empty, request, services, parse.GetValue(dryRun), parse.GetValue(JazzCli.JsonOption), token);
            }
            finally
            {
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }));

        return command;
    }

    private static Command BuildExport()
    {
        var project = new Argument<string>("project") { Description = "The .jazz file to export." };
        var output = new Option<string>("--out")
        {
            Description = "Where to write the file. Relative to the project; the extension picks the container.",
            Required = true,
        };
        var preset = new Option<string>("--preset")
        {
            Description = "youtube-1080p, youtube-4k, proof or lossless.",
            DefaultValueFactory = _ => ExportPresets.Default,
        };
        var mode = new Option<string>("--mode")
        {
            Description = "auto (copy when nothing needs rendering), copy, or encode.",
            DefaultValueFactory = _ => "auto",
        };
        var sequence = new Option<string?>("--sequence") { Description = "Which sequence. The active one when left out." };
        var snap = new Option<bool>("--snap-to-keyframes") { Description = "For a copy, move cuts to the nearest keyframe instead of refusing." };
        var inOut = new Option<bool>("--use-in-out") { Description = "Export only between the in and out points." };
        var external = new Option<bool>("--use-external-ffmpeg") { Description = "Encode through ffmpeg.exe." };
        var subtitles = new Option<string>("--subtitles")
        {
            Description = "What subtitle tracks become: soft (streams players can turn on), burn (drawn into the picture), sidecar (files beside the video) or none.",
            DefaultValueFactory = _ => "soft",
        };
        var sidecarFormat = new Option<string>("--sidecar-format") { Description = "srt, vtt or ass, for --subtitles sidecar.", DefaultValueFactory = _ => "srt" };
        var noChapters = new Option<bool>("--no-chapters") { Description = "Leave the chapter marks out of the file." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the plan and write nothing." };

        var command = new Command("export", "Export a sequence to a file in the foreground. 'jazz export enqueue' queues one in a running editor.")
        {
            project, output, preset, mode, sequence, snap, inOut, external, subtitles, sidecarFormat, noChapters, dryRun,
        };

        command.SetAction(parse => Guard(parse, token =>
        {
            string path = Path.GetFullPath(parse.GetValue(project)!);
            ProjectLoad load = ProjectFile.Load(path);
            if (!load.IsLoadable)
            {
                throw new CommandException("project-invalid", $"'{path}' has errors and will not open. Run 'jazz validate' to see them.");
            }

            using ServiceProvider services = new ServiceCollection().AddJazzHandsEngine().BuildServiceProvider();
            var request = new ExportRequest(
                parse.GetValue(output)!,
                parse.GetValue(preset)!,
                ParseMode(parse.GetValue(mode)!, allowAuto: true),
                parse.GetValue(sequence),
                parse.GetValue(snap),
                parse.GetValue(inOut),
                parse.GetValue(external),
                Choice<SubtitleDelivery>(parse.GetValue(subtitles)!, "subtitles"),
                Choice<SubtitleFormat>(parse.GetValue(sidecarFormat)!, "sidecar-format"),
                !parse.GetValue(noChapters));

            return Export(load.Project, path, request, services, parse.GetValue(dryRun), parse.GetValue(JazzCli.JsonOption), token);
        }));

        return command;
    }

    /// <summary>Plans, reports the plan, and unless it is a dry run, exports.</summary>
    private static int Export(Project project, string projectPath, ExportRequest request, IServiceProvider services, bool dryRun, bool json, CancellationToken token)
    {
        KeyframeLookup keyframes = services.GetRequiredService<KeyframeLookup>();
        ExportPlan plan = ExportPlanner.Plan(project, projectPath, request, keyframes, token);

        if (dryRun)
        {
            Console.Out.WriteLine(json
                ? JsonSerializer.Serialize(plan, JazzJson.Options)
                : Describe(plan));
            return ExitCode.Ok;
        }

        if (!json)
        {
            Console.Out.WriteLine(Describe(plan));
        }

        var meter = new ProgressLine(Console.Error, enabled: !json && !Console.IsErrorRedirected);
        ExportResult result = Exporter.Run(plan, project, projectPath, ExportEnvironment.Default, meter, token);
        meter.Done();

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new
                {
                    ok = true,
                    path = result.Path,
                    bytes = result.Bytes,
                    mode = plan.Mode,
                    encoder = result.Encoder,
                    frames = result.Frames,
                    seconds = result.Elapsed.TotalSeconds,
                    speed = result.Speed,
                    snaps = plan.Snaps,
                    notes = result.Notes,
                },
                JazzJson.Options));
        }
        else
        {
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote {result.Path}: {result.Bytes / 1048576.0:F1} MB, {Timecode.FormatClock(result.Duration)} in {result.Elapsed.TotalSeconds:F1} s ({result.Speed:F1}x real time) with {result.Encoder}."));
            foreach (string note in result.Notes)
            {
                Console.Out.WriteLine($"  {note}");
            }
        }

        return ExitCode.Ok;
    }

    /// <summary>The plan in a few lines a person or Claude Code can read.</summary>
    internal static string Describe(ExportPlan plan)
    {
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{(plan.Mode == ExportMode.Copy ? "Copy" : "Encode")} to {plan.OutputPath} ({plan.Container}), {Timecode.FormatClock(plan.Duration)}");
        text.AppendLine();

        foreach (string reason in plan.Reasons)
        {
            text.Append("  ").AppendLine(reason);
        }

        if (plan.Copy is { } copy)
        {
            text.Append(CultureInfo.InvariantCulture, $"  Picture: stream {copy.VideoStream}. Sound: ");
            text.AppendLine(copy.AudioStreams.IsEmpty
                ? "none."
                : string.Join(", ", copy.AudioStreams.Select((stream, index) =>
                    index < copy.StreamNames.Length ? $"{copy.StreamNames[index]} (stream {stream})" : $"stream {stream}")) + ".");

            int number = 1;
            foreach (TimeRange range in copy.SourceRanges)
            {
                text.Append(CultureInfo.InvariantCulture, $"  Stretch {number++}: {Timecode.FormatClock(range.Start)} to {Timecode.FormatClock(range.End)}");
                if (copy.FrameRate is { } rate)
                {
                    text.Append(CultureInfo.InvariantCulture, $" (frames {range.Start.ToFrames(rate, RoundingMode.Nearest)} to {range.End.ToFrames(rate, RoundingMode.Nearest)})");
                }

                text.AppendLine();
            }
        }
        else if (plan.Video is { } video)
        {
            text.Append(CultureInfo.InvariantCulture, $"  Picture: {video.Codec} {video.Width}x{video.Height} at {video.FrameRate} fps, {string.Join(" then ", video.Encoders)}");
            text.AppendLine(video.Lossless ? ", lossless." : $", quality {video.Quality}.");
            text.AppendLine(plan.Audio is { } audio
                ? string.Create(CultureInfo.InvariantCulture, $"  Sound: {audio.Encoder} {audio.Channels} channels at {audio.SampleRate} Hz.")
                : "  Sound: none.");
        }

        foreach (KeyframeSnap snap in plan.Snaps)
        {
            text.Append(CultureInfo.InvariantCulture, $"  Moved the {snap.Edge} at {Timecode.FormatClock(snap.Requested)} to the keyframe at {Timecode.FormatClock(snap.Snapped)} (frame {snap.Frame}).");
            text.AppendLine();
        }

        if (plan.Subtitles is { } subtitles)
        {
            foreach (ExportSubtitleTrack track in subtitles.Tracks)
            {
                string language = track.Language is { } code ? $" ({code})" : string.Empty;
                text.AppendLine(subtitles.Delivery switch
                {
                    SubtitleDelivery.Burn => $"  Subtitles: {track.Name}{language}, burned into the picture.",
                    SubtitleDelivery.Sidecar => $"  Subtitles: {track.Name}{language}, to {track.SidecarPath}.",
                    _ => $"  Subtitles: {track.Name}{language}, as a stream of {track.Codec}{(track.Default ? ", on by default" : string.Empty)}.",
                });
            }
        }

        if (!plan.Chapters.IsEmpty)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  Chapters: {string.Join(", ", plan.Chapters.Select(chapter => $"{chapter.Title} at {Timecode.FormatClock(chapter.Start)}"))}.");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The track playing a stream, by container index or by title.</summary>
    private static string LaneFor(Project project, MediaItem media, string stream)
    {
        Sequence sequence = project.ActiveSequence!;
        var lanes = QuickTrimOps.SoundTracks(sequence, media);

        (Track Track, int Stream, bool Enabled)? found = int.TryParse(stream, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            ? lanes.FirstOrDefault(lane => lane.Stream == index)
            : lanes.FirstOrDefault(lane => string.Equals(lane.Track.Name, stream, StringComparison.OrdinalIgnoreCase));

        if (found is not { Track: not null } lane)
        {
            string known = string.Join(", ", lanes.Select(entry => $"{entry.Stream} ({entry.Track.Name})"));
            throw new CommandException(
                "stream-not-found",
                $"'{media.Name}' has no sound stream '{stream}'. Its sound streams are {(known.Length > 0 ? known : "none")}.");
        }

        return lane.Track.Id;
    }

    private static void Run(Session session, ICommand command, CancellationToken token) =>
        session.ExecuteAsync(command, token).GetAwaiter().GetResult().EnsureOk();

    private static ExportMode ParseMode(string text, bool allowAuto) => text.Trim().ToLowerInvariant() switch
    {
        "copy" => ExportMode.Copy,
        "encode" => ExportMode.Encode,
        "auto" when allowAuto => ExportMode.Auto,
        _ => throw new CommandException(
            "invalid-value",
            allowAuto ? $"--mode takes auto, copy or encode, not '{text}'." : $"--mode takes copy or encode, not '{text}'."),
    };

    /// <summary>One of an enum's values by its lower case name, or a refusal that lists them.</summary>
    private static T Choice<T>(string text, string option)
        where T : struct, Enum =>
        Enum.TryParse(text.Trim(), ignoreCase: true, out T value) && Enum.IsDefined(value) && !int.TryParse(text, out _)
            ? value
            : throw new CommandException("invalid-value", $"--{option} takes {string.Join(", ", Enum.GetNames<T>().Select(name => name.ToLowerInvariant()))}, not '{text}'.");

    private static IEnumerable<string> Split(string? text) =>
        text is null ? [] : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string EnsureExtension(string path) =>
        path.EndsWith(ProjectPaths.Extension, StringComparison.OrdinalIgnoreCase) ? path : path + ProjectPaths.Extension;

    /// <summary>
    /// Runs a verb, turning Ctrl+C into a cancellation and every ordinary failure into an exit
    /// code and one sentence, never a stack trace.
    /// </summary>
    private static int Guard(ParseResult parse, Func<CancellationToken, int> body)
    {
        bool json = parse.GetValue(JazzCli.JsonOption);
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler stop = (_, args) =>
        {
            args.Cancel = true;
            cancel.Cancel();
        };

        Console.CancelKeyPress += stop;
        try
        {
            return body(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Fail(json, "cancelled", "Cancelled. Nothing was written.");
            return ExitCode.Cancelled;
        }
        catch (CommandException error)
        {
            Fail(json, error.Code, error.Message);
            return ExitCode.CommandError;
        }
        catch (ProjectFileException error)
        {
            Fail(json, "cannot-open", error.Message);
            return ExitCode.CommandError;
        }
        catch (FfmpegException error)
        {
            Fail(json, "media-error", error.Message);
            return ExitCode.MediaError;
        }
        catch (IOException error)
        {
            Fail(json, "io-error", error.Message);
            return ExitCode.CommandError;
        }
        finally
        {
            Console.CancelKeyPress -= stop;
        }
    }

    private static void Fail(bool json, string code, string message)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, code, error = message }, JazzJson.Options));
        }
        else
        {
            Console.Error.WriteLine($"jazz: {code}: {message}");
        }
    }

    /// <summary>One line on stderr that rewrites itself, when stderr is a terminal.</summary>
    private sealed class ProgressLine(TextWriter writer, bool enabled) : IProgress<ExportProgress>
    {
        private int _width;

        public void Report(ExportProgress value)
        {
            if (!enabled)
            {
                return;
            }

            string line = value.TotalFrames > 0
                ? string.Create(CultureInfo.InvariantCulture, $"  {value.Fraction * 100,5:F1}%  frame {value.Frame}/{value.TotalFrames}  {value.Fps:F0} fps  {value.Encoder}")
                : string.Create(CultureInfo.InvariantCulture, $"  {value.Fraction * 100,5:F1}%  {value.Bytes / 1048576.0:F1} MB  {value.Encoder}");

            _width = Math.Max(_width, line.Length);
            lock (writer)
            {
                writer.Write("\r" + line.PadRight(_width));
            }
        }

        public void Done()
        {
            if (enabled && _width > 0)
            {
                writer.Write("\r" + new string(' ', _width) + "\r");
            }
        }
    }
}
