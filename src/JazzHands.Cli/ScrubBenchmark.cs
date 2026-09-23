using System.Diagnostics;
using System.Globalization;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Frames;
using JazzHands.Media.Decode;
using JazzHands.Media.Import;
using JazzHands.Render;
using JazzHands.Render.Frames;

namespace JazzHands.Cli;

/// <summary>How the playhead moves while a scrub is measured.</summary>
public enum ScrubPattern
{
    /// <summary>
    /// Uniformly random times over the whole file. The worst case: every request is a seek into a
    /// different group of pictures, and nothing is ever cached.
    /// </summary>
    Random,

    /// <summary>
    /// Small steps, the way a hand dragging the playhead moves. What a scrub actually is, and
    /// what the decode-forward path and the frame cache are both built for.
    /// </summary>
    Drag,

    /// <summary>
    /// One frame at a time backwards, at playback rate. Each group of pictures is primed forwards
    /// into the cache as it is reached, because there is no way to decode backwards.
    /// </summary>
    Reverse,
}

/// <summary>What "jazz perf scrub" reports.</summary>
/// <param name="File">The file that was scrubbed.</param>
/// <param name="Path">Whether frames came off the GPU or the CPU.</param>
/// <param name="Pattern">How the playhead moved.</param>
/// <param name="Adapter">The graphics adapter, because the numbers mean nothing without it.</param>
/// <param name="Requests">How many times a frame was asked for.</param>
/// <param name="Served">How many came back.</param>
/// <param name="P50Milliseconds">The median request to texture time.</param>
/// <param name="P95Milliseconds">The ninety fifth percentile, which is what a person notices.</param>
/// <param name="MaxMilliseconds">The worst one.</param>
/// <param name="CacheHitRate">How much of the work the cache saved.</param>
/// <param name="DecodersOpened">How many decoders the pool had to open.</param>
/// <param name="LateFrames">Frames that took longer than the playback budget, for the reverse pattern.</param>
/// <param name="FrameBudgetMilliseconds">How long a frame may take at the source rate.</param>
public sealed record ScrubBenchmarkResult(
    string File,
    string Path,
    string Pattern,
    string Adapter,
    int Requests,
    int Served,
    double P50Milliseconds,
    double P95Milliseconds,
    double MaxMilliseconds,
    double CacheHitRate,
    long DecodersOpened,
    int LateFrames = 0,
    double FrameBudgetMilliseconds = 0);

/// <summary>
/// Measures how long a random seek takes to become a texture.
/// </summary>
/// <remarks>
/// This is the number that decides whether scrubbing feels connected to the mouse or not, which
/// is why it is an exit criterion and not a guess. Times are uniformly random over the file
/// rather than sequential: a sequential walk measures decode throughput, which
/// <c>jazz perf decode</c> already covers, and hides the seek that dominates a scrub.
///
/// The percentile matters more than the mean. A mean of 15 ms with a tail at 90 ms feels worse
/// than a flat 25 ms, because the tail is what the hand feels.
/// </remarks>
public static class ScrubBenchmark
{
    /// <summary>Scrubs a file at random times and reports the distribution.</summary>
    /// <param name="path">The media file.</param>
    /// <param name="requests">How many seeks to make.</param>
    /// <param name="useHardware">False to force the software decoder.</param>
    /// <param name="seed">The random seed, so a run is repeatable.</param>
    /// <param name="cacheBudgetBytes">What the frame cache may hold.</param>
    public static ScrubBenchmarkResult Run(
        string path,
        int requests = 200,
        bool useHardware = true,
        int seed = 20260923,
        ScrubPattern pattern = ScrubPattern.Random,
        SeekMode mode = SeekMode.Exact,
        long cacheBudgetBytes = FrameCache.DefaultBudgetBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requests);

        var importer = new MediaImporter();
        ImportedMedia imported = importer.Import(new ImportSource(System.IO.Path.GetFullPath(path), MediaKind.Movie));
        MediaItem item = imported.Item;

        using RenderDevice device = RenderDevice.Create();
        device.EnableMultithreadProtection();

        using HardwareDeviceContext? hardware = useHardware && device.SupportsVideo
            ? HardwareDeviceContext.CreateShared(device.Device.NativePointer, device.ImmediateContext.NativePointer)
            : null;

        Project project = Project.CreateNew("scrub") with
        {
            Media = new EquatableArray<MediaItem>([item]),
        };

        var clip = new Clip(
            Id.New(),
            new TimeRange(Flicks.Zero, item.Duration),
            Flicks.Zero,
            MediaId: item.Id);

        using var server = new SourceFrameServer(
            new DecoderPool(hardware),
            new FrameCache(new FrameTexturePool(device), cacheBudgetBytes),
            device);

        var random = new Random(seed);
        var times = new List<double>(requests);
        int served = 0;
        string decodePath = "software";

        // One warm-up so the first measurement is not paying for the decoder being opened.
        server.GetSourceFrame(project, clip, Flicks.Zero);

        if (pattern == ScrubPattern.Reverse)
        {
            return Reverse(path, item, project, clip, server, device, requests, mode);
        }

        Flicks position = new(item.Duration.Value / 2);
        int dragStep = 1;

        for (int request = 0; request < requests; request++)
        {
            Flicks at;

            if (pattern == ScrubPattern.Random)
            {
                at = new Flicks((long)(random.NextDouble() * item.Duration.Value));
            }
            else
            {
                // A hand dragging the playhead: a few frames at a time, turning round now and
                // then, which is what the decode-forward path and the cache are both built for.
                if (random.NextDouble() < 0.05)
                {
                    dragStep = -dragStep;
                }

                position += new Flicks(dragStep * Flicks.PerSecond / 30);

                if (position < Flicks.Zero || position >= item.Duration)
                {
                    dragStep = -dragStep;
                    position = new Flicks(Math.Clamp(position.Value, 0, item.Duration.Value - 1));
                }

                at = position;
            }

            var clock = Stopwatch.StartNew();
            FrameTexture? frame = server.GetSourceFrame(project, clip, at, mode: mode);
            clock.Stop();

            times.Add(clock.Elapsed.TotalMilliseconds);

            if (frame is not null)
            {
                served++;
            }
        }

        if (server.Copied > 0)
        {
            decodePath = "hardware";
        }

        times.Sort();

        return new ScrubBenchmarkResult(
            System.IO.Path.GetFileName(path),
            decodePath,
            $"{pattern.ToString().ToLowerInvariant()}, {mode.ToString().ToLowerInvariant()}",
            device.AdapterName,
            requests,
            served,
            Percentile(times, 0.50),
            Percentile(times, 0.95),
            times[^1],
            server.Cache.HitRate,
            server.Decoders.Opened);
    }

    /// <summary>
    /// Walks backwards through the file one frame at a time, priming each group of pictures as it
    /// is reached.
    /// </summary>
    /// <remarks>
    /// Reverse playback is the case that cannot be done by seeking, because a frame depends on
    /// the ones before it. Each group is decoded forwards into the cache once and then handed out
    /// in the other order, so the cost is one group's decode spread over one group's worth of
    /// frames. What this measures is whether that spread is small enough to keep up.
    /// </remarks>
    private static ScrubBenchmarkResult Reverse(
        string path,
        MediaItem item,
        Project project,
        Clip clip,
        SourceFrameServer server,
        RenderDevice device,
        int requests,
        SeekMode mode)
    {
        Rational rate = item.Info?.VideoStreams.FirstOrDefault()?.FrameRate ?? Rational.Fps30;
        KeyframeIndex index = KeyframeIndex.Build(System.IO.Path.GetFullPath(path), 0);

        var times = new List<double>(requests);
        int served = 0;
        long primedAt = -1;
        double budget = 1000.0 / rate.ToDouble();

        long lastFrame = item.Duration.ToFrames(rate, RoundingMode.Floor) - 1;

        for (int step = 0; step < requests; step++)
        {
            long frameIndex = lastFrame - step;
            if (frameIndex < 0)
            {
                break;
            }

            Flicks at = Flicks.FromFrames(frameIndex, rate);

            var clock = Stopwatch.StartNew();

            // Reaching a new group means decoding all of it forwards before any of it can be
            // shown backwards. That decode is part of what the frame costs, so it is inside the
            // measurement rather than beside it.
            long gopStart = index.AtOrBefore(at).Value;
            if (gopStart != primedAt)
            {
                server.PrimeGop(project, clip, at, index);
                primedAt = gopStart;
            }

            FrameTexture? frame = server.GetSourceFrame(project, clip, at, direction: PlayDirection.Reverse, mode: mode);
            clock.Stop();

            times.Add(clock.Elapsed.TotalMilliseconds);

            if (frame is not null)
            {
                served++;
            }
        }

        times.Sort();

        return new ScrubBenchmarkResult(
            System.IO.Path.GetFileName(path),
            server.Copied > 0 ? "hardware" : "software",
            $"reverse at {rate.ToDisplayString()} fps",
            device.AdapterName,
            times.Count,
            served,
            Percentile(times, 0.50),
            Percentile(times, 0.95),
            times.Count == 0 ? 0 : times[^1],
            server.Cache.HitRate,
            server.Decoders.Opened,
            LateFrames: times.Count(time => time > budget),
            FrameBudgetMilliseconds: budget);
    }

    /// <summary>Formats a result for a person.</summary>
    public static string Describe(ScrubBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             {result.File}
               path         {result.Path} on {result.Adapter}
               pattern      {result.Pattern}
               requests     {result.Requests}, {result.Served} served
               p50          {result.P50Milliseconds:F1} ms
               p95          {result.P95Milliseconds:F1} ms
               worst        {result.MaxMilliseconds:F1} ms
               cache hits   {result.CacheHitRate:P0}
               decoders     {result.DecodersOpened} opened{LateLine(result)}
             """);
    }

    /// <summary>The late frame line, which only a playback pattern has anything to say about.</summary>
    private static string LateLine(ScrubBenchmarkResult result) =>
        result.FrameBudgetMilliseconds <= 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Environment.NewLine}  late         {result.LateFrames} of {result.Requests} over the "
                + $"{result.FrameBudgetMilliseconds:F1} ms frame budget");

    /// <summary>
    /// <summary>
    /// The value at a percentile of a sorted list, by nearest rank.
    /// </summary>
    /// <remarks>
    /// Nearest rank rather than interpolation: these are measurements that happened, and the
    /// ninety fifth percentile should be one of them rather than a number between two of them.
    /// </remarks>
    private static double Percentile(List<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        int rank = (int)Math.Ceiling(fraction * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}
