using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Frames;
using JazzHands.Media.Encode;
using JazzHands.Render;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Cli;

/// <summary>
/// The verbs that let Claude Code look at an edit: <c>jazz frame</c>, <c>jazz frames</c>,
/// <c>jazz contact-sheet</c> and <c>jazz proof</c>.
/// </summary>
/// <remarks>
/// <c>frame</c> and <c>frames</c> draw exactly as the editor's preview does, so what is looked at is
/// what a person sees there; <c>contact-sheet</c> sends <c>export.contact-sheet</c> and <c>proof</c>
/// exports with the proof preset, so those are what an export writes. None of them changes the
/// project.
/// </remarks>
public static class InspectCommands
{
    /// <summary>Adds the verbs to the root command.</summary>
    public static void AddTo(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Subcommands.Add(BuildFrame());
        root.Subcommands.Add(BuildFrames());
        root.Subcommands.Add(BuildContactSheet());
        root.Subcommands.Add(BuildProof());
    }

    private static Command BuildFrame()
    {
        var project = new Argument<string>("project") { Description = "The .jazz file." };
        var at = new Option<string?>("--at") { Description = "The sequence time to draw: 00:00:12.500, 750f or 12.5s. Required without --sheet." };
        var output = new Option<string>("--out") { Description = "The .png to write, relative to the current folder.", Required = true };
        var size = new Option<string?>("--size") { Description = "How wide to draw it, as a size: 960x540 or 1080p; the height follows the sequence's shape. The sequence's own when left out; 1920 wide for a sheet." };
        var sequence = new Option<string?>("--sequence") { Description = "Which sequence; the active one when left out." };
        var sheet = new Option<bool>("--sheet") { Description = "Draw several frames side by side, labelled with their times: an animated effect or a template judged from one picture." };
        var times = new Option<string>("--times") { Description = "With --sheet: fractions of the way through, 0 the start and 1 the last frame.", DefaultValueFactory = _ => "0,0.25,0.5,0.75,1" };
        var clip = new Option<string?>("--clip") { Description = "With --sheet: through this clip rather than the whole sequence." };

        var command = new Command("frame", "Draw one frame of a sequence to a PNG exactly as the editor's preview shows it, to look at, or with --sheet several across a clip. 'jazz export still' writes the frame an export would.")
        {
            project, at, output, size, sequence, sheet, times, clip,
        };

        command.SetAction(parse => ExportCommands.Guard(parse, token =>
        {
            (Project loaded, string path) = Load(parse.GetValue(project)!);
            Sequence chosen = SequenceOf(loaded, parse.GetValue(sequence));
            Rational rate = loaded.SettingsFor(chosen).FrameRate;
            string file = Path.GetFullPath(parse.GetValue(output)!);
            if (parse.GetValue(sheet))
            {
                double[] fractions = [.. parse.GetValue(times)!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                        ? value
                        : throw new CommandException("invalid-value", $"'{text}' is not a fraction; --times takes numbers from 0 to 1, as 0,0.5,1."))];
                int across = Math.Min(fractions.Length, 5);
                int sheetWidth = ParseSize(parse.GetValue(size), rate)?.Width ?? 1920;
                Send(loaded, path, new ContactSheetCommand(file, across, Rows: 1, Width: sheetWidth, SequenceId: chosen.Id, Times: fractions, ClipId: parse.GetValue(clip)), token);
                return Wrote(parse, file, $"{fractions.Length} frames", new { frames = fractions.Length });
            }

            var time = (Flicks)CommandValues.Parse(
                typeof(Flicks),
                parse.GetValue(at) ?? throw new CommandException("missing-argument", "Give --at, the time to draw, or --sheet for several."),
                rate,
                "at")!;
            int width = ParseSize(parse.GetValue(size), rate)?.Width ?? loaded.SettingsFor(chosen).Width;

            using RenderDevice device = RenderDevice.Create();
            using var renderer = new StillRenderer(device);
            (int drawnWidth, int drawnHeight) = Draw(renderer, loaded, chosen, time, width, path, file);
            return Wrote(parse, file, $"{drawnWidth}x{drawnHeight}, frame {time.ToFrames(rate, RoundingMode.Floor)} at {Timecode.FormatClock(time)}", new { at = time, frame = time.ToFrames(rate, RoundingMode.Floor), width = drawnWidth, height = drawnHeight });
        }));

        return command;
    }

    /// <summary>One frame as the preview draws it, written as a PNG.</summary>
    private static (int Width, int Height) Draw(StillRenderer renderer, Project project, Sequence sequence, Flicks time, int width, string projectPath, string file)
    {
        if (time < Flicks.Zero || time >= sequence.Duration)
        {
            throw new CommandException("time-out-of-range", $"{Timecode.FormatClock(time)} is outside '{sequence.Name}', which is {Timecode.FormatClock(sequence.Duration)} long.");
        }

        if (!string.Equals(Path.GetExtension(file), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandException("unsupported-container", $"A frame is written as a PNG; '{file}' is not a .png file.");
        }

        (int drawnWidth, int drawnHeight, byte[] bgra) = renderer.RenderPreview(project, sequence, time, width, projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
        PngWriter.Write(file, drawnWidth, drawnHeight, bgra);
        return (drawnWidth, drawnHeight);
    }

    private static Command BuildFrames()
    {
        var project = new Argument<string>("project") { Description = "The .jazz file." };
        var every = new Option<string>("--every") { Description = "How far apart: 2s, 00:00:05.000, 120f.", Required = true };
        var output = new Option<string>("--out") { Description = "The folder to write the PNGs into; made when it is not there.", Required = true };
        var size = new Option<string?>("--size") { Description = "How wide to draw each, as a size: 640x360; the height follows the sequence's shape.", DefaultValueFactory = _ => "640x360" };
        var sequence = new Option<string?>("--sequence") { Description = "Which sequence; the active one when left out." };
        var range = new Option<string?>("--range") { Description = "Only this stretch: 00:10-00:30. All of the sequence when left out." };

        var command = new Command("frames", "Draw a frame every so often through a sequence as the preview shows it, one PNG each, named by their times.")
        {
            project, every, output, size, sequence, range,
        };

        command.SetAction(parse => ExportCommands.Guard(parse, token =>
        {
            (Project loaded, string path) = Load(parse.GetValue(project)!);
            Sequence chosen = SequenceOf(loaded, parse.GetValue(sequence));
            Rational rate = loaded.SettingsFor(chosen).FrameRate;
            var step = (Flicks)CommandValues.Parse(typeof(Flicks), parse.GetValue(every)!, rate, "every")!;
            if (step <= Flicks.Zero)
            {
                throw new CommandException("invalid-value", "--every has to be longer than nothing.");
            }

            TimeRange span = parse.GetValue(range) is { Length: > 0 } text
                ? (TimeRange)CommandValues.Parse(typeof(TimeRange), text, rate, "range")!
                : new TimeRange(Flicks.Zero, chosen.Duration);
            string folder = Path.GetFullPath(parse.GetValue(output)!);
            Directory.CreateDirectory(folder);
            int width = ParseSize(parse.GetValue(size), rate)?.Width ?? loaded.SettingsFor(chosen).Width;

            var written = new List<string>();
            int index = 1;
            using RenderDevice device = RenderDevice.Create();
            using var renderer = new StillRenderer(device);
            for (Flicks time = span.Start; time < span.End && time < chosen.Duration; time += step, index++)
            {
                token.ThrowIfCancellationRequested();
                string file = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{index:0000}_{Timecode.FormatClock(time).Replace(':', '-')}.png"));
                Draw(renderer, loaded, chosen, time, width, path, file);
                written.Add(file);
            }

            if (parse.GetValue(JazzCli.JsonOption))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, folder, files = written }, JazzJson.Options));
            }
            else
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {written.Count} frame(s) to {folder}, every {Timecode.FormatClock(step)} from {Timecode.FormatClock(span.Start)}."));
            }

            return ExitCode.Ok;
        }));

        return command;
    }

    private static Command BuildContactSheet()
    {
        var project = new Argument<string>("project") { Description = "The .jazz file." };
        var output = new Option<string>("--out") { Description = "The .png to write.", Required = true };
        var columns = new Option<int>("--cols") { Description = "Tiles across.", DefaultValueFactory = _ => 6 };
        var rows = new Option<int>("--rows") { Description = "Tiles down.", DefaultValueFactory = _ => 4 };
        var width = new Option<int>("--width") { Description = "The sheet's width in pixels.", DefaultValueFactory = _ => 1920 };
        var sequence = new Option<string?>("--sequence") { Description = "Which sequence; the active one when left out." };
        var range = new Option<string?>("--range") { Description = "Only this stretch: 00:10-00:30." };

        var command = new Command("contact-sheet", "Tile frames from even steps through a sequence into one PNG, each labelled with its time: a whole edit at a glance.")
        {
            project, output, columns, rows, width, sequence, range,
        };

        command.SetAction(parse => ExportCommands.Guard(parse, token =>
        {
            (Project loaded, string path) = Load(parse.GetValue(project)!);
            Rational rate = RateOf(loaded, parse.GetValue(sequence));
            TimeRange? span = parse.GetValue(range) is { Length: > 0 } text ? (TimeRange)CommandValues.Parse(typeof(TimeRange), text, rate, "range")! : null;
            string file = Path.GetFullPath(parse.GetValue(output)!);

            Send(
                loaded,
                path,
                new ContactSheetCommand(file, parse.GetValue(columns), parse.GetValue(rows), parse.GetValue(width), parse.GetValue(sequence), span?.Start, span?.End),
                token);
            return Wrote(parse, file, $"{parse.GetValue(columns)} by {parse.GetValue(rows)} frames", new { columns = parse.GetValue(columns), rows = parse.GetValue(rows) });
        }));

        return command;
    }

    private static Command BuildProof()
    {
        var project = new Argument<string>("project") { Description = "The .jazz file." };
        var output = new Option<string>("--out") { Description = "Where to write it, relative to the project: proof.mp4.", Required = true };
        var sequence = new Option<string?>("--sequence") { Description = "Which sequence; the active one when left out." };
        var range = new Option<string?>("--range") { Description = "Only this stretch: 00:10-00:30." };
        var encoder = new Option<string?>("--encoder") { Description = "The encoders to try, in order: libx264 keeps it off the GPU." };

        var command = new Command("proof", "Export a quick 480p proof of a sequence, every frame rendered, to watch before handing off.")
        {
            project, output, sequence, range, encoder,
        };

        command.SetAction(parse => ExportCommands.Guard(parse, token =>
        {
            (Project loaded, string path) = Load(parse.GetValue(project)!);
            Rational rate = RateOf(loaded, parse.GetValue(sequence));
            TimeRange? span = parse.GetValue(range) is { Length: > 0 } text ? (TimeRange)CommandValues.Parse(typeof(TimeRange), text, rate, "range")! : null;
            ExportOverrides? overrides = parse.GetValue(encoder) is { Length: > 0 } names
                ? new ExportOverrides(Encoders: new EquatableArray<string>([.. names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]))
                : null;

            using ServiceProvider services = new ServiceCollection().AddJazzHandsEngine().BuildServiceProvider();
            var request = new ExportRequest(parse.GetValue(output)!, "proof", ExportMode.Encode, parse.GetValue(sequence), Overrides: overrides, Range: span);
            return ExportCommands.Export(loaded, path, request, services, dryRun: false, parse.GetValue(JazzCli.JsonOption), token);
        }));

        return command;
    }

    private static (Project Project, string Path) Load(string file)
    {
        string path = Path.GetFullPath(file);
        ProjectLoad load = ProjectFile.Load(path);
        return load.IsLoadable
            ? (load.Project, path)
            : throw new CommandException("project-invalid", $"'{path}' has errors and will not open. Run 'jazz validate' to see them.");
    }

    private static Sequence SequenceOf(Project project, string? id) =>
        (id is null ? project.ActiveSequence : project.Sequence(id))
            ?? throw new CommandException("sequence-not-found", id is null ? "The project has no sequence." : $"No sequence with id '{id}'.");

    private static Rational RateOf(Project project, string? sequence) => project.SettingsFor(SequenceOf(project, sequence)).FrameRate;

    private static FrameSize? ParseSize(string? text, Rational rate) =>
        text is { Length: > 0 } ? (FrameSize)CommandValues.Parse(typeof(FrameSize), text, rate, "size")! : null;

    /// <summary>Sends one command through a headless session of the project, which is not saved.</summary>
    private static void Send(Project project, string path, ICommand command, CancellationToken token)
    {
        using ServiceProvider services = new ServiceCollection().AddJazzHandsEngine().BuildServiceProvider();
        var session = new Session(project, services, path);
        try
        {
            session.ExecuteAsync(command, token).GetAwaiter().GetResult().EnsureOk();
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int Wrote(ParseResult parse, string file, string what, object details)
    {
        if (parse.GetValue(JazzCli.JsonOption))
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, path = file, details }, JazzJson.Options));
        }
        else
        {
            Console.Out.WriteLine($"Wrote {file}: {what}.");
        }

        return ExitCode.Ok;
    }
}
