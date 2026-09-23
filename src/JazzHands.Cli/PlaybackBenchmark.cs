using System.Diagnostics;
using System.Globalization;
using JazzHands.Audio.Output;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Diagnostics;
using JazzHands.Engine.Playback;
using JazzHands.Media.Import;
using JazzHands.Render;
using JazzHands.Render.Passes;
using Vortice.Direct3D11;

namespace JazzHands.Cli;

/// <summary>What "jazz perf playback" reports.</summary>
/// <param name="File">The media the sequence was built from.</param>
/// <param name="Size">Its frame size.</param>
/// <param name="FrameRate">Its frame rate, as a ratio.</param>
/// <param name="Adapter">The GPU it played on.</param>
/// <param name="Device">The sound card whose clock it played to, or "silent".</param>
/// <param name="Clips">How many clips the sequence had.</param>
/// <param name="Seconds">How long it played, by the wall clock.</param>
/// <param name="FramesDue">Frames the clock passed through while playing at normal speed.</param>
/// <param name="Presented">Frames handed to the preview target.</param>
/// <param name="Dropped">Frames that were due and never shown. The criterion is under 0.1%.</param>
/// <param name="DroppedPercent">Dropped as a share of frames due.</param>
/// <param name="LateP50Milliseconds">Median time between a frame becoming due and being presented.</param>
/// <param name="LateP99Milliseconds">The same at the 99th percentile.</param>
/// <param name="PresentP99Milliseconds">99th percentile of the blit and GPU wait the target did per frame.</param>
/// <param name="Underruns">Times the sound card ran dry.</param>
/// <param name="Notices">What the session noticed: decoder fallbacks, files it could not read.</param>
public sealed record PlaybackBenchmarkResult(
    string File,
    string Size,
    string FrameRate,
    string Adapter,
    string Device,
    int Clips,
    double Seconds,
    long FramesDue,
    long Presented,
    long Dropped,
    double DroppedPercent,
    double LateP50Milliseconds,
    double LateP99Milliseconds,
    double PresentP99Milliseconds,
    long Underruns,
    string[] Notices);

/// <summary>
/// Plays a long sequence through the real playback engine and counts the frames it drops.
/// </summary>
/// <remarks>
/// The sequence is one file laid end to end enough times to fill the run, so a five minute run of
/// a five second file crosses sixty edits, each one a jump back to the start of the source. The
/// clock is the sound card's, as in the editor, at zero monitor volume unless asked otherwise.
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
    /// <param name="progress">Where to report as it goes.</param>
    public static PlaybackBenchmarkResult Run(
        string path,
        TimeSpan duration,
        bool hardware = true,
        bool audible = false,
        int panelWidth = 2560,
        int panelHeight = 1440,
        TextWriter? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        MediaItem item = new MediaImporter().Import(new ImportSource(Path.GetFullPath(path), MediaKind.Movie)).Item;
        MediaStream video = item.Info?.VideoStreams.FirstOrDefault()
            ?? throw new InvalidOperationException($"'{path}' has no picture to play.");

        (Project project, int clips) = Build(item, video, duration);
        ProjectSettings settings = project.Settings;

        using RenderDevice device = RenderDevice.Create();

        IAudioOutput output = WasapiOutput.HasDevice()
            ? new WasapiOutput(settings.SampleRate, settings.ChannelCount)
            : new SilentAudioOutput(settings.SampleRate, settings.ChannelCount);

        using var transport = new Transport(output) { MonitorGain = audible ? 1.0f : 0.0f };
        transport.Load(project);

        var notices = new DiagnosticsLog();
        using var engine = new PlaybackEngine(transport, device, new PlaybackOptions { HardwareDecode = hardware }, notices);
        using var target = new PanelTarget(device, panelWidth, panelHeight);

        engine.AddTarget(target);
        engine.Load(project);
        engine.Quality = PreviewQuality.Full;
        engine.WaitForPresent(TimeSpan.FromSeconds(30));

        engine.Play();
        if (!transport.WaitUntilRolling(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("Playback never started.");
        }

        target.StartCounting();
        long presentedAtStart = engine.PresentedFrames;
        long droppedAtStart = engine.DroppedFrames;
        Flicks startedAt = transport.Position;
        var watch = Stopwatch.StartNew();
        TimeSpan nextReport = TimeSpan.FromSeconds(30);

        while (watch.Elapsed < duration && transport.State == TransportState.Playing)
        {
            Thread.Sleep(100);

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
        engine.Pause();

        long due = endedAt.ToFrames(settings.FrameRate, RoundingMode.Floor) - startedAt.ToFrames(settings.FrameRate, RoundingMode.Floor);
        long dropped = engine.DroppedFrames - droppedAtStart;

        return new PlaybackBenchmarkResult(
            Path.GetFileName(path),
            $"{video.Width}x{video.Height}",
            settings.FrameRate.ToString(),
            device.AdapterName,
            output.DeviceName,
            clips,
            seconds,
            due,
            engine.PresentedFrames - presentedAtStart,
            dropped,
            due == 0 ? 0 : dropped * 100.0 / due,
            target.LatePercentile(0.50),
            target.LatePercentile(0.99),
            target.PresentPercentile(0.99),
            transport.Underruns,
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
            {result.File}: {result.Size} at {result.FrameRate} fps, {result.Clips} clips, on {result.Adapter}, clock from {result.Device}
              {result.Seconds:F1} s, {result.FramesDue} frames due, {result.Presented} presented
              dropped         {result.Dropped} ({result.DroppedPercent:F3}%)
              late            p50 {result.LateP50Milliseconds:F1} ms, p99 {result.LateP99Milliseconds:F1} ms after the frame was due
              present         p99 {result.PresentP99Milliseconds:F2} ms for the blit and GPU wait
              underruns       {result.Underruns}
              notices         {notices}
            {(result.DroppedPercent < 0.1 ? "Inside the bar of 0.1%." : "Over the bar of 0.1%.")}
            """);
    }

    /// <summary>The file end to end until the run is covered, with one to spare.</summary>
    private static (Project Project, int Clips) Build(MediaItem item, MediaStream video, TimeSpan duration)
    {
        Rational rate = video.FrameRate ?? Rational.Fps30;
        Flicks length = Flicks.FromFrames(item.Duration.ToFrames(rate, RoundingMode.Floor), rate);
        int clips = (int)Math.Ceiling(Flicks.FromSeconds(duration.TotalSeconds).Value / (double)length.Value) + 1;

        var laid = new List<Clip>(clips);
        for (int index = 0; index < clips; index++)
        {
            laid.Add(new Clip(
                Id.New(),
                new TimeRange(length * index, length),
                Flicks.Zero,
                MediaId: item.Id,
                SourceStreamIndex: video.Index,
                Name: $"{item.Name} {index}"));
        }

        var settings = new ProjectSettings(rate, video.Width, video.Height);
        Project project = Project.CreateNew("perf playback", settings) with { Media = EquatableArray.Create(item) };

        var track = new Track(Id.New(), TrackKind.Video, "V1", 0, new EquatableArray<Clip>(laid));
        return (project with { Sequences = EquatableArray.Create(project.Sequences[0] with { Tracks = [track] }) }, clips);
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
