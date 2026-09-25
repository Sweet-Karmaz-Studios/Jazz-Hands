using System.Diagnostics;
using System.Globalization;
using JazzHands.Audio.Output;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Playback;
using JazzHands.Media.Import;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Cli;

/// <summary>One kind of edit's times.</summary>
/// <param name="Command">The command's name.</param>
/// <param name="Count">How many were timed.</param>
/// <param name="P50Milliseconds">The median, from asking to the change being published.</param>
/// <param name="P99Milliseconds">The 99th percentile.</param>
/// <param name="MaxMilliseconds">The slowest.</param>
public sealed record EditTiming(string Command, int Count, double P50Milliseconds, double P99Milliseconds, double MaxMilliseconds);

/// <summary>What "jazz perf project" reports.</summary>
/// <param name="Clips">Clips in the project, over all its tracks.</param>
/// <param name="Tracks">Tracks, half picture and half sound.</param>
/// <param name="FileBytes">The size of the saved .jazz file.</param>
/// <param name="FirstLoadMilliseconds">The first load in the process, which pays for building the serializer's type information.</param>
/// <param name="LoadMilliseconds">The median of five loads after that: what opening a second project costs.</param>
/// <param name="SessionMilliseconds">Opening a session over the loaded project with the transport following it: the mix built twice.</param>
/// <param name="Edits">Each kind of edit's times, the transport's rebuild of the mix included.</param>
/// <param name="AllEdits">Every edit together.</param>
public sealed record ProjectBenchmarkResult(
    int Clips,
    int Tracks,
    long FileBytes,
    double FirstLoadMilliseconds,
    double LoadMilliseconds,
    double SessionMilliseconds,
    EditTiming[] Edits,
    EditTiming AllEdits);

/// <summary>
/// Times a big project: loading it, opening a session over it, and the edits a person makes,
/// each from the command being asked for to the new project being published to everything that
/// follows the session.
/// </summary>
/// <remarks>
/// The project is one media file cut into clips three seconds long with a second's gap between
/// them, over four picture and four sound tracks, the way a long edit of one recording looks. Each
/// edit is undone straight after, so the project stays the size it started. The transport follows
/// the session on a silent output, as it does in the editor, because rebuilding the mix after
/// every command is part of what an edit costs. The timeline's own redraw is measured in
/// <c>TimelinePerformanceTests</c>; this is the engine's half of the 16 ms an edit may take.
/// </remarks>
public static class ProjectBenchmark
{
    private const int TrackPairs = 4;

    /// <summary>Builds, saves, loads and edits a project of <paramref name="clips"/> clips.</summary>
    /// <param name="path">A media file with a picture and sound.</param>
    /// <param name="clips">How many clips, over all the tracks.</param>
    /// <param name="edits">How many rounds of edits; each round is a move, a trim and a split, each undone.</param>
    public static async Task<ProjectBenchmarkResult> RunAsync(string path, int clips, int edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        MediaItem item = new MediaImporter().Import(new ImportSource(Path.GetFullPath(path), MediaKind.Movie)).Item;
        Project built = Build(item, Math.Max(TrackPairs * 2, clips));

        string folder = Path.Combine(Path.GetTempPath(), "jazz-perf-project", Core.Model.Id.New());
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "stress.jazz");
            ProjectFile.Save(file, built);

            long started = Stopwatch.GetTimestamp();
            ProjectLoad load = ProjectFile.Load(file);
            double first = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var loads = new List<double>();
            for (int run = 0; run < 5; run++)
            {
                started = Stopwatch.GetTimestamp();
                load = ProjectFile.Load(file);
                loads.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            Project project = load.Project;
            ProjectSettings settings = project.Settings;

            using ServiceProvider services = new ServiceCollection().AddJazzHandsEngine().BuildServiceProvider();
            using var transport = new Transport(new SilentAudioOutput(settings.SampleRate, settings.ChannelCount));

            started = Stopwatch.GetTimestamp();
            await using var session = new Session(project, services, file) { DefaultIssuer = "perf" };
            transport.Attach(session);
            double opened = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var times = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            string[] ids = [.. project.Sequences[0].Tracks.SelectMany(track => track.Clips).Select(clip => clip.Id)];
            var random = new Random(0x2000);

            for (int round = 0; round < edits; round++)
            {
                string id = ids[random.Next(ids.Length)];
                Clip clip = session.Project.Sequences[0].Tracks.SelectMany(track => track.Clips).First(candidate => candidate.Id == id);
                Flicks quarter = Flicks.FromSeconds(0.25);
                Flicks middle = clip.Range.Start + (clip.Range.Duration / 2);

                await Timed(session, times, new MoveClipCommand(id, clip.Range.Start + quarter)).ConfigureAwait(false);
                await Timed(session, times, new UndoCommand()).ConfigureAwait(false);
                await Timed(session, times, new TrimClipCommand(id, Out: clip.Range.End - quarter)).ConfigureAwait(false);
                await Timed(session, times, new UndoCommand()).ConfigureAwait(false);
                await Timed(session, times, new SplitClipCommand(id, middle)).ConfigureAwait(false);
                await Timed(session, times, new UndoCommand()).ConfigureAwait(false);
                await Timed(session, times, new RedoCommand()).ConfigureAwait(false);
                await Timed(session, times, new UndoCommand()).ConfigureAwait(false);
            }

            transport.Detach();

            EditTiming[] each = [.. times.Select(pair => Summarise(pair.Key, pair.Value))];
            EditTiming all = Summarise("all", [.. times.Values.SelectMany(list => list)]);

            return new ProjectBenchmarkResult(
                ids.Length,
                project.Sequences[0].Tracks.Length,
                new FileInfo(file).Length,
                Math.Round(first, 1),
                Math.Round(Median(loads), 1),
                Math.Round(opened, 1),
                each,
                all);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A human summary.</summary>
    public static string Describe(ProjectBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = new System.Text.StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{result.Clips} clips on {result.Tracks} tracks, {result.FileBytes / 1024} KB");
        text.AppendLine(CultureInfo.InvariantCulture, $"  load            first {result.FirstLoadMilliseconds:F1} ms, then {result.LoadMilliseconds:F1} ms");
        text.AppendLine(CultureInfo.InvariantCulture, $"  session         {result.SessionMilliseconds:F1} ms with the transport's mix");
        foreach (EditTiming edit in result.Edits.Append(result.AllEdits))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {edit.Command,-15} p50 {edit.P50Milliseconds:F2} ms, p99 {edit.P99Milliseconds:F2} ms, max {edit.MaxMilliseconds:F2} ms over {edit.Count}");
        }

        text.Append(result.AllEdits.P99Milliseconds < 16.0 ? "Inside the 16 ms an edit may take." : "Over the 16 ms an edit may take.");
        return text.ToString();
    }

    private static async Task Timed(Session session, Dictionary<string, List<double>> times, ICommand command)
    {
        long started = Stopwatch.GetTimestamp();
        CommandResult result = await session.ExecuteAsync(command).ConfigureAwait(false);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        string name = CommandRegistry.NameOf(command);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"{name} was refused: {result.Error}");
        }

        if (!times.TryGetValue(name, out List<double>? list))
        {
            list = [];
            times[name] = list;
        }

        list.Add(elapsed);
    }

    private static Project Build(MediaItem item, int clips)
    {
        Core.Model.MediaStream video = item.Info?.VideoStreams.FirstOrDefault()
            ?? throw new InvalidOperationException($"'{item.Name}' has no picture.");
        Core.Model.MediaStream? audio = item.Info.AudioStreams.FirstOrDefault();

        Rational rate = video.FrameRate ?? Rational.Fps30;
        var settings = new ProjectSettings(rate, video.Width, video.Height);
        Project project = Project.CreateNew("perf project", settings) with { Media = EquatableArray.Create(item) };

        Flicks length = Flicks.FromSeconds(3);
        Flicks step = Flicks.FromSeconds(4);
        long sourceFrames = item.Duration.ToFrames(rate, RoundingMode.Floor);
        Flicks sourceRoom = Flicks.FromFrames(sourceFrames, rate) - length;

        int trackCount = audio is null ? TrackPairs : TrackPairs * 2;
        var tracks = new List<Track>(trackCount);
        for (int track = 0; track < trackCount; track++)
        {
            bool sound = track >= TrackPairs;
            int count = (clips / trackCount) + (track < clips % trackCount ? 1 : 0);
            var laid = new Clip[count];
            for (int index = 0; index < count; index++)
            {
                // Each clip from somewhere else in the file, as a real edit's are.
                Flicks sourceIn = sourceRoom.Value <= 0 ? Flicks.Zero : new Flicks((long)((ulong)(index * 7919 + track * 104729) % (ulong)sourceRoom.Value));
                sourceIn = Flicks.FromFrames(sourceIn.ToFrames(rate, RoundingMode.Floor), rate);
                laid[index] = new Clip(
                    Core.Model.Id.New(),
                    new TimeRange(step * index, length),
                    sourceIn,
                    MediaId: item.Id,
                    SourceStreamIndex: sound ? audio!.Index : video.Index,
                    Name: string.Create(CultureInfo.InvariantCulture, $"{item.Name} {track + 1}.{index + 1}"));
            }

            string name = sound ? $"A{track - TrackPairs + 1}" : $"V{track + 1}";
            tracks.Add(new Track(Core.Model.Id.New(), sound ? TrackKind.Audio : TrackKind.Video, name, track, new EquatableArray<Clip>(laid)));
        }

        return project with { Sequences = EquatableArray.Create(project.Sequences[0] with { Tracks = [.. tracks] }) };
    }

    private static EditTiming Summarise(string command, List<double> times)
    {
        times.Sort();
        return new EditTiming(
            command,
            times.Count,
            Math.Round(Percentile(times, 0.50), 2),
            Math.Round(Percentile(times, 0.99), 2),
            Math.Round(times[^1], 2));
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return Percentile(values, 0.5);
    }

    private static double Percentile(List<double> sorted, double fraction) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * fraction) - 1, 0, sorted.Count - 1)];
}
