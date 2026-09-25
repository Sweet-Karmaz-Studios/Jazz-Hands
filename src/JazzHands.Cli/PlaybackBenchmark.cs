using System.Diagnostics;
using System.Globalization;
using System.Numerics;
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
using JazzHands.Render.Passes;
using Microsoft.Extensions.DependencyInjection;
using Vortice.Direct3D11;

namespace JazzHands.Cli;

/// <summary>What "jazz perf playback" reports.</summary>
/// <param name="File">The media the sequence was built from.</param>
/// <param name="Size">Its frame size.</param>
/// <param name="FrameRate">Its frame rate, as a ratio.</param>
/// <param name="Adapter">The GPU it played on.</param>
/// <param name="Device">The sound card whose clock it played to, or "silent".</param>
/// <param name="Layers">How many video tracks played at once, each its own decoder lane.</param>
/// <param name="Clips">How many clips the sequence had, over all its layers.</param>
/// <param name="Effects">The picture effects stacked on every clip, first to last.</param>
/// <param name="Seconds">How long it played, by the wall clock.</param>
/// <param name="FramesDue">Frames the clock passed through while playing at normal speed.</param>
/// <param name="Presented">Frames handed to the preview target.</param>
/// <param name="Dropped">Frames that were due and never shown. The criterion is under 0.1%.</param>
/// <param name="DroppedPercent">Dropped as a share of frames due.</param>
/// <param name="LateP50Milliseconds">Median time between a frame becoming due and being presented.</param>
/// <param name="LateP99Milliseconds">The same at the 99th percentile.</param>
/// <param name="PresentP99Milliseconds">99th percentile of the blit and GPU wait the target did per frame.</param>
/// <param name="Underruns">Times the sound card ran dry.</param>
/// <param name="TargetsCreated">Render targets the compositor created in steady play, from two seconds in. Zero is the bar.</param>
/// <param name="FrameTexturesCreated">Frame textures created in steady play, from two seconds in.</param>
/// <param name="Notices">What the session noticed: decoder fallbacks, files it could not read.</param>
public sealed record PlaybackBenchmarkResult(
    string File,
    string Size,
    string FrameRate,
    string Adapter,
    string Device,
    int Layers,
    int Clips,
    string[] Effects,
    double Seconds,
    long FramesDue,
    long Presented,
    long Dropped,
    double DroppedPercent,
    double LateP50Milliseconds,
    double LateP99Milliseconds,
    double PresentP99Milliseconds,
    long Underruns,
    long TargetsCreated,
    long FrameTexturesCreated,
    string[] Notices);

/// <summary>
/// Plays a long sequence through the real playback engine and counts the frames it drops.
/// </summary>
/// <remarks>
/// The sequence is one file laid end to end enough times to fill the run, so a five minute run of
/// a five second file crosses sixty edits, each one a jump back to the start of the source. The
/// clock is the sound card's, as in the editor, at zero monitor volume unless asked otherwise.
/// With <c>--layers</c> the same is stacked on several video tracks, each transformed into a
/// quadrant, which is the compositor's load: a decode per layer, the source, transform and
/// composite passes for each, and one output pass.
/// With <c>--effects</c> every clip carries the same stack of picture effects at their default
/// settings, run in order at the working resolution each frame.
///
/// Each frame is presented to a target that does what the preview panel's presenter does: a
/// filtered blit of the program texture into a panel-sized surface and a wait for the GPU to
/// finish. The one thing missing is WPF taking the surface, which spike S1 measured on its own at
/// a few microseconds; <c>Docs/spikes/S1.md</c> has the numbers.
/// </remarks>
public static class PlaybackBenchmark
{
    /// <summary>Builds the sequence and plays it.</summary>
    /// <param name="path">A media file with a video stream.</param>
    /// <param name="duration">How long to play.</param>
    /// <param name="hardware">Decode on the GPU where it can.</param>
    /// <param name="audible">Hear it.</param>
    /// <param name="panelWidth">The width of the surface the target presents into.</param>
    /// <param name="panelHeight">Its height.</param>
    /// <param name="layers">How many video tracks to stack, each transformed into its own quadrant.</param>
    /// <param name="effects">Picture effects to stack on every clip, at their default settings, by type id.</param>
    /// <param name="progress">Where to report as it goes.</param>
    /// <param name="vfx">Add Phase 29a's load over the file: two moving layers with motion blur, a particle layer, and a heavy hit every four seconds.</param>
    public static PlaybackBenchmarkResult Run(
        string path,
        TimeSpan duration,
        bool hardware = true,
        bool audible = false,
        int panelWidth = 2560,
        int panelHeight = 1440,
        int layers = 1,
        IReadOnlyList<string>? effects = null,
        TextWriter? progress = null,
        bool vfx = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        MediaItem item = new MediaImporter().Import(new ImportSource(Path.GetFullPath(path), MediaKind.Movie)).Item;
        MediaStream video = item.Info?.VideoStreams.FirstOrDefault()
            ?? throw new InvalidOperationException($"'{path}' has no picture to play.");

        string[] stacked = [.. effects ?? []];
        (Project project, int clips) = Build(item, video, duration, Math.Max(1, layers), stacked);
        if (vfx)
        {
            (project, int added) = Vfx(project, duration);
            clips += added;
            stacked = [.. stacked, "motion blur x2", "gen.particles.embers", "impact.heavy every 4 s"];
        }

        ProjectSettings settings = project.Settings;

        using RenderDevice device = RenderDevice.Create();

        IAudioOutput output = WasapiOutput.HasDevice()
            ? new WasapiOutput(settings.SampleRate, settings.ChannelCount)
            : new SilentAudioOutput(settings.SampleRate, settings.ChannelCount);

        using var transport = new Transport(output) { MonitorGain = audible ? 1.0f : 0.0f };
        transport.Load(project);

        var notices = new DiagnosticsLog();
        // The target before the engine, so it is disposed after: the engine's thread can still be
        // presenting when the run ends, and a present into a released query waits forever.
        using var target = new PanelTarget(device, panelWidth, panelHeight);
        using var engine = new PlaybackEngine(transport, device, new PlaybackOptions { HardwareDecode = hardware }, notices);

        engine.AddTarget(target);
        engine.Load(project);
        engine.Quality = PreviewQuality.Full;
        engine.WaitForPresent(TimeSpan.FromSeconds(30));

        // Press play the way a person does, after the first frame is up and the ones after it are
        // decoded, not the instant the file opens.
        SpinWait.SpinUntil(() => engine.IsPrerolled, TimeSpan.FromSeconds(30));

        engine.Play();
        if (!transport.WaitUntilRolling(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("Playback never started.");
        }

        target.StartCounting();
        long presentedAtStart = engine.PresentedFrames;
        long droppedAtStart = engine.DroppedFrames;
        RenderStatsInfo? statsAtStart = null;
        Flicks startedAt = transport.Position;
        var watch = Stopwatch.StartNew();
        TimeSpan nextReport = TimeSpan.FromSeconds(30);

        while (watch.Elapsed < duration && transport.State == TransportState.Playing)
        {
            Thread.Sleep(100);

            // Allocation is counted from two seconds in, once the frame cache has filled to its
            // budget: what matters is that steady playback allocates nothing.
            if (statsAtStart is null && watch.Elapsed >= TimeSpan.FromSeconds(2))
            {
                statsAtStart = engine.RenderStats;
            }

            if (progress is not null && watch.Elapsed >= nextReport)
            {
                progress.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{watch.Elapsed:mm\\:ss}  presented {engine.PresentedFrames - presentedAtStart}, dropped {engine.DroppedFrames - droppedAtStart}, underruns {transport.Underruns}"));
                nextReport += TimeSpan.FromSeconds(30);
            }
        }

        Flicks endedAt = transport.Position;
        double seconds = watch.Elapsed.TotalSeconds;
        RenderStatsInfo statsAtEnd = engine.RenderStats;
        statsAtStart ??= statsAtEnd;
        engine.Pause();

        long due = endedAt.ToFrames(settings.FrameRate, RoundingMode.Floor) - startedAt.ToFrames(settings.FrameRate, RoundingMode.Floor);
        long dropped = engine.DroppedFrames - droppedAtStart;

        return new PlaybackBenchmarkResult(
            Path.GetFileName(path),
            $"{video.Width}x{video.Height}",
            settings.FrameRate.ToString(),
            device.AdapterName,
            output.DeviceName,
            Math.Max(1, layers),
            clips,
            stacked,
            seconds,
            due,
            engine.PresentedFrames - presentedAtStart,
            dropped,
            due == 0 ? 0 : dropped * 100.0 / due,
            target.LatePercentile(0.50),
            target.LatePercentile(0.99),
            target.PresentPercentile(0.99),
            transport.Underruns,
            statsAtEnd.TargetsCreated - statsAtStart.TargetsCreated,
            statsAtEnd.FrameTexturesCreated - statsAtStart.FrameTexturesCreated,
            [.. notices.All.Select(notice => $"{notice.Code}: {notice.Message}")]);
    }

    /// <summary>A human summary.</summary>
    public static string Describe(PlaybackBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        string notices = result.Notices.Length == 0 ? "none" : string.Join("; ", result.Notices);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            {result.File}: {result.Size} at {result.FrameRate} fps, {result.Layers} layer(s), {result.Clips} clips, {(result.Effects.Length == 0 ? "no effects" : string.Join(" + ", result.Effects))}, on {result.Adapter}, clock from {result.Device}
              {result.Seconds:F1} s, {result.FramesDue} frames due, {result.Presented} presented
              dropped         {result.Dropped} ({result.DroppedPercent:F3}%)
              late            p50 {result.LateP50Milliseconds:F1} ms, p99 {result.LateP99Milliseconds:F1} ms after the frame was due
              present         p99 {result.PresentP99Milliseconds:F2} ms for the blit and GPU wait
              underruns       {result.Underruns}
              allocated       {result.TargetsCreated} render targets, {result.FrameTexturesCreated} frame textures in steady play
              notices         {notices}
            {(result.DroppedPercent < 0.1 ? "Inside the bar of 0.1%." : "Over the bar of 0.1%.")}
            """);
    }

    /// <summary>
    /// The file end to end until the run is covered, with one to spare, on each of the layers.
    /// </summary>
    /// <remarks>
    /// With more than one layer, each is scaled to half and rotated a little into its own quadrant,
    /// so every layer goes through the transform pass and all of them are visible. Each starts a
    /// different distance into the file, because the frame cache is keyed by source time: layers
    /// showing the same frame would share one decode and the run would measure a quarter of the
    /// work it claims to.
    /// </remarks>
    private static (Project Project, int Clips) Build(MediaItem item, MediaStream video, TimeSpan duration, int layers, string[] effects)
    {
        Rational rate = video.FrameRate ?? Rational.Fps30;
        long fileFrames = item.Duration.ToFrames(rate, RoundingMode.Floor);
        Flicks length = Flicks.FromFrames(fileFrames, rate);
        Flicks run = Flicks.FromSeconds(duration.TotalSeconds);

        var settings = new ProjectSettings(rate, video.Width, video.Height);
        Project project = Project.CreateNew("perf playback", settings) with { Media = EquatableArray.Create(item) };

        EquatableArray<Effect> chain = new([.. effects.Select(Effect.Create)]);
        var tracks = new List<Track>(layers);
        int total = 0;

        for (int layer = 0; layer < layers; layer++)
        {
            Flicks offset = Flicks.FromFrames(fileFrames * layer / layers, rate);
            Transform? transform = layers == 1 ? null : Quadrant(layer, video.Width, video.Height);
            var laid = new List<Clip>();
            Flicks at = Flicks.Zero;

            while (at <= run + length)
            {
                // The first clip on a layer starts part way into the file; the rest are whole.
                Flicks sourceIn = laid.Count == 0 ? offset : Flicks.Zero;
                laid.Add(new Clip(
                    Id.New(),
                    new TimeRange(at, length - sourceIn),
                    sourceIn,
                    MediaId: item.Id,
                    SourceStreamIndex: video.Index,
                    Transform: transform,
                    Effects: chain,
                    Name: $"{item.Name} {layer + 1}.{laid.Count}"));
                at += length - sourceIn;
            }

            tracks.Add(new Track(Id.New(), TrackKind.Video, $"V{layer + 1}", layer, new EquatableArray<Clip>(laid)));
            total += laid.Count;
        }

        return (project with { Sequences = EquatableArray.Create(project.Sequences[0] with { Tracks = [.. tracks] }) }, total);
    }

    /// <summary>
    /// Phase 29a's bar over the picture: a title and a shape crossing the frame every two seconds
    /// with motion blur, embers over the whole run, and a heavy hit every four seconds, placed by
    /// the real command so the benchmark plays what a person makes.
    /// </summary>
    private static (Project Project, int Clips) Vfx(Project project, TimeSpan duration)
    {
        using ServiceProvider services = new ServiceCollection().AddJazzHandsEngine().BuildServiceProvider();
        var session = new Session(project, services);
        Flicks run = Flicks.FromSeconds(duration.TotalSeconds + 5);
        float width = project.Settings.Width;
        string[] ids = [Id.New(), Id.New(), Id.New()];
        string[] tracks = [Id.New(), Id.New(), Id.New()];
        var commands = new List<ICommand>();
        for (int index = 0; index < 3; index++)
        {
            commands.Add(new AddTrackCommand(TrackKind.Video, TrackId: tracks[index]));
        }

        commands.Add(new AddTitleCommand(Flicks.Zero, "BOSS RUSH", "title-card", run, tracks[0], ClipId: ids[0]));
        commands.Add(new AddClipCommand(tracks[1], Flicks.Zero, GeneratorId: "gen.shape.rectangle", Duration: run, ClipId: ids[1]));
        commands.Add(new AddClipCommand(tracks[2], Flicks.Zero, GeneratorId: "gen.particles.embers", Duration: run, ClipId: ids[2]));

        // Back and forth across the frame, a second each way.
        for (int second = 0; second <= (int)run.ToSeconds(); second++)
        {
            string across = FormattableString.Invariant($"{(second % 2 == 0 ? -0.4f : 0.4f) * width:0}, {(second % 2 == 0 ? -200 : 200)}");
            commands.Add(new AddKeyframeCommand(ids[0], "transform.position", Flicks.FromSeconds(second), across));
            commands.Add(new AddKeyframeCommand(ids[1], "transform.position", Flicks.FromSeconds(second), across.StartsWith('-') ? across[1..] : "-" + across));
        }

        commands.Add(new SetClipMotionBlurCommand(ids[0], Angle: 180));
        commands.Add(new SetClipMotionBlurCommand(ids[1], Angle: 180));
        for (double hit = 2; hit < run.ToSeconds() - 2; hit += 4)
        {
            commands.Add(new ApplyVfxPresetCommand("impact.heavy", At: Flicks.FromSeconds(hit)));
        }

        CommandResult result = session.ExecuteAsync(commands, "vfx load").GetAwaiter().GetResult();
        if (!result.Ok)
        {
            throw new InvalidOperationException($"The vfx scene did not build: {result.Code}: {result.Error}");
        }

        Project built = session.Project;
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return (built, built.ActiveSequence!.Tracks.Sum(track => track.Clips.Length) - project.ActiveSequence!.Tracks.Sum(track => track.Clips.Length));
    }

    /// <summary>Half size, turned a few degrees, in one quadrant of the frame.</summary>
    private static Transform Quadrant(int layer, int width, int height)
    {
        float x = (layer % 2 == 0 ? -0.25f : 0.25f) * width;
        float y = ((layer / 2) % 2 == 0 ? -0.25f : 0.25f) * height;

        return Transform.Identity with
        {
            Position = AnimatedValue.Constant(new ParamValue.Float2(new Vector2(x, y))),
            Scale = AnimatedValue.Constant(new ParamValue.Float2(new Vector2(0.5f, 0.5f))),
            Rotation = AnimatedValue.Constant(layer % 2 == 0 ? 3.0f : -3.0f),
        };
    }

    /// <summary>Does what the preview panel's presenter does, minus handing the surface to WPF.</summary>
    private sealed class PanelTarget : IPreviewTarget, IDisposable
    {
        private readonly RenderDevice _device;
        private readonly PreviewPass _pass;
        private readonly ID3D11Texture2D _surface;
        private readonly ID3D11RenderTargetView _view;
        private readonly ID3D11Query _done;
        private readonly int _width;
        private readonly int _height;
        private readonly List<double> _late = new(1 << 16);
        private readonly List<double> _present = new(1 << 16);
        private volatile bool _counting;

        public PanelTarget(RenderDevice device, int width, int height)
        {
            _device = device;
            _width = width;
            _height = height;
            _pass = new PreviewPass(device);
            _surface = device.CreateRenderTarget(width, height, Vortice.DXGI.Format.B8G8R8A8_UNorm);
            _view = device.Device.CreateRenderTargetView(_surface);
            _done = device.Device.CreateQuery(new QueryDescription { QueryType = QueryType.Event });
        }

        public void StartCounting()
        {
            lock (_late)
            {
                _late.Clear();
                _present.Clear();
                _counting = true;
            }
        }

        public bool Present(in PreviewFrame frame)
        {
            long start = Stopwatch.GetTimestamp();

            _pass.Clear(_view);
            _pass.Blit(frame.Texture, _view, _width, _height, QuadRect.Fit(frame.SequenceWidth, frame.SequenceHeight, _width, _height));

            ID3D11DeviceContext context = _device.ImmediateContext;
            context.End(_done);
            context.Flush();
            var spin = default(SpinWait);
            while (!context.GetData(_done, out int finished) || finished == 0)
            {
                spin.SpinOnce();
            }

            if (_counting)
            {
                lock (_late)
                {
                    _present.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    _late.Add((frame.Playhead - frame.Time).Value * 1000.0 / Flicks.PerSecond);
                }
            }

            return true;
        }

        public double LatePercentile(double fraction) => Percentile(_late, fraction);

        public double PresentPercentile(double fraction) => Percentile(_present, fraction);

        public void Dispose()
        {
            _done.Dispose();
            _view.Dispose();
            _surface.Dispose();
            _pass.Dispose();
        }

        private double Percentile(List<double> values, double fraction)
        {
            lock (_late)
            {
                if (values.Count == 0)
                {
                    return 0;
                }

                double[] sorted = [.. values.Order()];
                return sorted[Math.Min(sorted.Length - 1, (int)(fraction * sorted.Length))];
            }
        }
    }
}
