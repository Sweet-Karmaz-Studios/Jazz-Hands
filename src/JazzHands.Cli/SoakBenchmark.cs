using System.Diagnostics;
using System.Globalization;
using JazzHands.Audio.Output;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Diagnostics;
using JazzHands.Engine.Playback;
using JazzHands.Media.Import;
using JazzHands.Render;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Cli;

/// <summary>One minute of a soak: what the process held.</summary>
/// <param name="Minute">Minutes since playing began.</param>
/// <param name="ManagedMb">The managed heap after a full collection: what is really kept.</param>
/// <param name="WorkingSetMb">The process's working set.</param>
/// <param name="PrivateMb">Its private bytes.</param>
/// <param name="VideoMb">Video memory the process holds, from the adapter.</param>
/// <param name="Handles">Open handles.</param>
/// <param name="Threads">Threads.</param>
/// <param name="TargetsCreated">Render targets the compositor has ever created.</param>
/// <param name="TexturesCreated">Frame textures ever created.</param>
/// <param name="Presented">Frames presented.</param>
/// <param name="Dropped">Frames dropped.</param>
/// <param name="Edits">Edits made.</param>
public sealed record SoakSample(
    int Minute,
    double ManagedMb,
    double WorkingSetMb,
    double PrivateMb,
    double VideoMb,
    int Handles,
    int Threads,
    long TargetsCreated,
    long TexturesCreated,
    long Presented,
    long Dropped,
    long Edits);

/// <summary>How fast something grew over the soak, from a straight line fitted after the warm-up.</summary>
/// <param name="What">What was measured.</param>
/// <param name="First">Its value when the warm-up ended.</param>
/// <param name="Last">Its value at the end.</param>
/// <param name="PerHour">The fitted growth per hour.</param>
/// <param name="Limit">The most growth per hour that counts as steady.</param>
public sealed record SoakGrowth(string What, double First, double Last, double PerHour, double Limit)
{
    /// <summary>True when it grew no faster than its limit.</summary>
    public bool Steady => PerHour <= Limit;
}

/// <summary>What "jazz perf soak" reports.</summary>
/// <param name="File">The media played.</param>
/// <param name="Minutes">How long it ran.</param>
/// <param name="Edits">Edits made while it played.</param>
/// <param name="Seeks">Jumps to somewhere else.</param>
/// <param name="PausedMinutes">Minutes it stood aside, paused, because something else was using the GPU.</param>
/// <param name="Growth">How each measure grew after the warm-up.</param>
/// <param name="Samples">A sample a minute.</param>
public sealed record SoakBenchmarkResult(
    string File,
    double Minutes,
    long Edits,
    long Seeks,
    double PausedMinutes,
    SoakGrowth[] Growth,
    SoakSample[] Samples)
{
    /// <summary>True when nothing grew faster than its limit.</summary>
    public bool Steady => Growth.All(growth => growth.Steady);
}

/// <summary>
/// Plays and edits for hours and watches what the process holds: the Phase 32 soak.
/// </summary>
/// <remarks>
/// The sequence is the file end to end on two layers with a grade on every clip, looped. Every
/// few seconds something is edited through the session, the way a person works while it plays:
/// a clip moved and the move undone, an effect added and removed, a clip split and put back, the
/// preview quality changed; now and then the playhead jumps somewhere else, which opens and closes
/// decoders. A sample a minute, after a full collection, goes to the CSV as it is taken, so a run
/// cut short still says something. Growth is the slope of a line fitted to the samples after the
/// first ten minutes, which is when the caches have filled to their budgets. Silent.
///
/// The machine is shared: when nvidia-smi says the GPU is busier than this run makes it (a game),
/// playing and editing pause until it is quiet again for a minute, and the samples go on, since
/// memory that grows while nothing happens is worth knowing about too.
/// </remarks>
public static class SoakBenchmark
{
    private const int WarmupMinutes = 10;
    private const int BusyPercent = 30;

    /// <summary>Runs the soak.</summary>
    /// <param name="path">A media file with a picture.</param>
    /// <param name="duration">How long.</param>
    /// <param name="csv">Where to write the samples as they are taken, or null.</param>
    /// <param name="progress">Where to say how it is going, or null.</param>
    public static SoakBenchmarkResult Run(string path, TimeSpan duration, string? csv, TextWriter? progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        MediaItem item = new MediaImporter().Import(new ImportSource(Path.GetFullPath(path), MediaKind.Movie)).Item;
        Project project = Build(item);
        ProjectSettings settings = project.Settings;

        using ServiceProvider services = new ServiceCollection().AddJazzHandsEngine().BuildServiceProvider();
        var session = new Session(project, services) { DefaultIssuer = "soak" };
        using RenderDevice device = RenderDevice.Create();

        IAudioOutput output = WasapiOutput.HasDevice()
            ? new WasapiOutput(settings.SampleRate, settings.ChannelCount)
            : new SilentAudioOutput(settings.SampleRate, settings.ChannelCount);
        using var transport = new Transport(output) { MonitorGain = 0.0f };
        transport.Attach(session);

        using var target = new PlaybackBenchmark.PanelTarget(device, 1920, 1080);
        using var engine = new PlaybackEngine(transport, device, new PlaybackOptions(), new DiagnosticsLog());
        engine.AddTarget(target);
        engine.Attach(session);
        engine.Loop = true;
        engine.WaitForPresent(TimeSpan.FromSeconds(30));
        SpinWait.SpinUntil(() => engine.IsPrerolled, TimeSpan.FromSeconds(30));
        engine.Play();

        using StreamWriter? writer = csv is null ? null : new StreamWriter(csv, append: false) { AutoFlush = true };
        writer?.WriteLine("minute,managed_mb,working_set_mb,private_mb,video_mb,handles,threads,targets_created,textures_created,presented,dropped,edits");

        var samples = new List<SoakSample>();
        var random = new Random(0x50A4);
        var watch = Stopwatch.StartNew();
        TimeSpan nextSample = TimeSpan.Zero;
        long edits = 0;
        long seeks = 0;
        bool standingAside = false;
        TimeSpan nextCheck = TimeSpan.FromMinutes(1);
        TimeSpan asideSince = TimeSpan.Zero;
        TimeSpan aside = TimeSpan.Zero;

        try
        {
            while (watch.Elapsed < duration)
            {
                if (watch.Elapsed >= nextSample)
                {
                    SoakSample sample = Sample((int)Math.Round(watch.Elapsed.TotalMinutes), device, engine, edits);
                    samples.Add(sample);
                    writer?.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{sample.Minute},{sample.ManagedMb:F1},{sample.WorkingSetMb:F1},{sample.PrivateMb:F1},{sample.VideoMb:F1},{sample.Handles},{sample.Threads},{sample.TargetsCreated},{sample.TexturesCreated},{sample.Presented},{sample.Dropped},{sample.Edits}"));
                    progress?.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{watch.Elapsed:hh\\:mm}  managed {sample.ManagedMb:F0} MB, working set {sample.WorkingSetMb:F0} MB, video {sample.VideoMb:F0} MB, handles {sample.Handles}, edits {edits}"));
                    nextSample += TimeSpan.FromMinutes(1);
                }

                if (watch.Elapsed >= nextCheck)
                {
                    nextCheck += TimeSpan.FromMinutes(1);
                    bool busy = GpuPercent() is { } percent && percent > BusyPercent;
                    if (busy && !standingAside)
                    {
                        engine.Pause();
                        standingAside = true;
                        asideSince = watch.Elapsed;
                        progress?.WriteLine("Something else is using the GPU; paused until it stops.");
                    }
                    else if (!busy && standingAside)
                    {
                        standingAside = false;
                        aside += watch.Elapsed - asideSince;
                        progress?.WriteLine("The GPU is quiet again; playing.");
                    }
                }

                Thread.Sleep(TimeSpan.FromSeconds(3));
                if (standingAside)
                {
                    continue;
                }

                edits += Edit(session, engine, random, ref seeks);

                if (transport.State != TransportState.Playing)
                {
                    engine.Play();
                }
            }
        }
        finally
        {
            engine.Pause();
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (standingAside)
        {
            aside += watch.Elapsed - asideSince;
        }

        return new SoakBenchmarkResult(Path.GetFileName(path), Math.Round(watch.Elapsed.TotalMinutes, 1), edits, seeks, Math.Round(aside.TotalMinutes, 1), Growth(samples), [.. samples]);
    }

    /// <summary>A human summary.</summary>
    public static string Describe(SoakBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = new System.Text.StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{result.File}: {result.Minutes:F0} minutes, {result.Edits} edits, {result.Seeks} jumps, {result.PausedMinutes:F0} minutes paused for another program");
        foreach (SoakGrowth growth in result.Growth)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {growth.What,-16} {growth.First,8:F1} to {growth.Last,8:F1}, {growth.PerHour,7:F2} an hour (steady under {growth.Limit})");
        }

        text.Append(result.Growth.Length == 0
            ? $"Too short to say: growth is fitted after the first {WarmupMinutes} minutes."
            : result.Steady ? "Steady." : "Growing.");
        return text.ToString();
    }

    /// <summary>One edit, or a jump, through the session; returns how many edits it made.</summary>
    private static long Edit(Session session, PlaybackEngine engine, Random random, ref long seeks)
    {
        Sequence sequence = session.Project.Sequences[0];
        Track track = sequence.Tracks[random.Next(sequence.Tracks.Length)];
        Clip clip = track.Clips[random.Next(track.Clips.Length)];

        switch (random.Next(5))
        {
            case 0:
                Run(session, new MoveClipCommand(clip.Id, clip.Range.Start));
                Run(session, new UndoCommand());
                return 2;
            case 1:
                CommandResult added = Run(session, new AddEffectCommand(clip.Id, "video.blur.gaussian"));
                Run(session, new UndoCommand());
                return added.Ok ? 2 : 1;
            case 2:
                Run(session, new SplitClipCommand(clip.Id, clip.Range.Start + (clip.Range.Duration / 2)));
                Run(session, new UndoCommand());
                return 2;
            case 3:
                engine.Quality = engine.Quality == PreviewQuality.Full ? PreviewQuality.Half : PreviewQuality.Full;
                return 0;
            default:
                Flicks end = sequence.Tracks.Max(candidate => candidate.Clips.Length == 0 ? Flicks.Zero : candidate.Clips[^1].Range.End);
                engine.Seek(new Flicks((long)(random.NextDouble() * end.Value * 0.9)));
                seeks++;
                return 0;
        }
    }

    /// <summary>How busy the GPU is by nvidia-smi, or null when there is no nvidia-smi to ask.</summary>
    private static int? GpuPercent()
    {
        try
        {
            using var smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (smi is null)
            {
                return null;
            }

            string text = smi.StandardOutput.ReadToEnd();
            smi.WaitForExit(5000);
            return int.TryParse(text.Split('\n')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent) ? percent : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static CommandResult Run(Session session, ICommand command) =>
        session.ExecuteAsync(command).GetAwaiter().GetResult();

    private static SoakSample Sample(int minute, RenderDevice device, PlaybackEngine engine, long edits)
    {
        long managed = GC.GetTotalMemory(forceFullCollection: true);
        using var process = Process.GetCurrentProcess();
        RenderStatsInfo stats = engine.RenderStats;
        return new SoakSample(
            minute,
            managed / 1048576.0,
            process.WorkingSet64 / 1048576.0,
            process.PrivateMemorySize64 / 1048576.0,
            device.QueryVideoMemory().UsedBytes / 1048576.0,
            process.HandleCount,
            process.Threads.Count,
            stats.TargetsCreated,
            stats.FrameTexturesCreated,
            engine.PresentedFrames,
            engine.DroppedFrames,
            edits);
    }

    /// <summary>Straight lines through the samples after the warm-up, per hour.</summary>
    private static SoakGrowth[] Growth(List<SoakSample> samples)
    {
        SoakSample[] settled = [.. samples.Where(sample => sample.Minute >= WarmupMinutes)];
        if (settled.Length < 3)
        {
            return [];
        }

        return
        [
            Fit("managed MB", settled, sample => sample.ManagedMb, 5),
            Fit("working set MB", settled, sample => sample.WorkingSetMb, 20),
            Fit("private MB", settled, sample => sample.PrivateMb, 20),
            Fit("video MB", settled, sample => sample.VideoMb, 20),
            Fit("handles", settled, sample => sample.Handles, 50),
            Fit("targets created", settled, sample => sample.TargetsCreated, 10),
        ];
    }

    private static SoakGrowth Fit(string what, SoakSample[] samples, Func<SoakSample, double> value, double limit)
    {
        double meanX = samples.Average(sample => sample.Minute);
        double meanY = samples.Average(value);
        double covariance = samples.Sum(sample => (sample.Minute - meanX) * (value(sample) - meanY));
        double variance = samples.Sum(sample => (sample.Minute - meanX) * (sample.Minute - meanX));
        double perMinute = variance == 0 ? 0 : covariance / variance;
        return new SoakGrowth(what, Math.Round(value(samples[0]), 1), Math.Round(value(samples[^1]), 1), Math.Round(perMinute * 60, 2), limit);
    }

    /// <summary>The file end to end on two layers, each clip graded, enough for ten minutes.</summary>
    private static Project Build(MediaItem item)
    {
        MediaStream video = item.Info?.VideoStreams.FirstOrDefault()
            ?? throw new InvalidOperationException($"'{item.Name}' has no picture to play.");
        Rational rate = video.FrameRate ?? Rational.Fps30;
        Flicks length = Flicks.FromFrames(item.Duration.ToFrames(rate, RoundingMode.Floor), rate);
        var settings = new ProjectSettings(rate, video.Width, video.Height);
        Project project = Project.CreateNew("soak", settings) with { Media = EquatableArray.Create(item) };

        int count = (int)Math.Ceiling(600.0 / length.ToSeconds());
        var tracks = new List<Track>();
        for (int layer = 0; layer < 2; layer++)
        {
            var clips = new Clip[count];
            for (int index = 0; index < count; index++)
            {
                clips[index] = new Clip(
                    Id.New(),
                    new TimeRange(length * index, length),
                    Flicks.Zero,
                    MediaId: item.Id,
                    SourceStreamIndex: video.Index,
                    Effects: EquatableArray.Create(Effect.Create("color.basic")),
                    Opacity: AnimatedValue.Constant(layer == 0 ? 1.0f : 0.5f),
                    Name: string.Create(CultureInfo.InvariantCulture, $"{item.Name} {layer + 1}.{index + 1}"));
            }

            tracks.Add(new Track(Id.New(), TrackKind.Video, $"V{layer + 1}", layer, new EquatableArray<Clip>(clips)));
        }

        return project with { Sequences = EquatableArray.Create(project.Sequences[0] with { Tracks = [.. tracks] }) };
    }
}
