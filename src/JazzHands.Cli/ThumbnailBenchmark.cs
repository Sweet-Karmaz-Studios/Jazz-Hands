using System.Diagnostics;
using System.Globalization;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Caching;
using JazzHands.Media.Import;
using JazzHands.Media.Waveforms;
using Microsoft.Data.Sqlite;

namespace JazzHands.Cli;

/// <summary>What "jazz perf thumbs" reports, for one zoom.</summary>
/// <param name="PixelsPerSecond">The zoom.</param>
/// <param name="Spacing">Seconds of source between thumbnails at that zoom.</param>
/// <param name="Visible">Thumbnails on a 1500 pixel screen.</param>
/// <param name="Strip">Thumbnails in the whole clip's strip.</param>
/// <param name="VisibleMilliseconds">From adding the clip to every visible thumbnail being ready.</param>
/// <param name="StripMilliseconds">From adding the clip to every thumbnail of the whole strip being shown, a keyframe near it at least.</param>
/// <param name="ExactMilliseconds">From adding the clip to every thumbnail being the one its grid asks for, when the workers go idle.</param>
public sealed record ThumbnailZoomResult(
    double PixelsPerSecond,
    double Spacing,
    int Visible,
    int Strip,
    double VisibleMilliseconds,
    double StripMilliseconds,
    double ExactMilliseconds);

/// <summary>What "jazz perf thumbs" reports.</summary>
/// <param name="File">The file.</param>
/// <param name="Duration">How long it is, in seconds.</param>
/// <param name="Picture">Its picture, as the probe put it.</param>
/// <param name="Workers">Background workers making thumbnails.</param>
/// <param name="ImportMilliseconds">Hashing and probing: the part of adding a file before anything can be asked of it.</param>
/// <param name="Zooms">Each zoom measured, from a cold cache.</param>
/// <param name="WaveformMilliseconds">From adding the clip to its first sound stream's waveform being complete, or -1 without sound.</param>
/// <param name="WarmStripMilliseconds">The last zoom's strip again from the disk cache alone, in a fresh service.</param>
public sealed record ThumbnailBenchmarkResult(
    string File,
    double Duration,
    string Picture,
    int Workers,
    double ImportMilliseconds,
    IReadOnlyList<ThumbnailZoomResult> Zooms,
    double WaveformMilliseconds,
    double WarmStripMilliseconds);

/// <summary>
/// Measures how soon a clip's thumbnails and waveform appear after it is added, from a cold cache.
/// </summary>
/// <remarks>
/// The Phase 14 exit criterion: for a ten minute 4K clip, the visible thumbnails within a second,
/// the whole strip within twenty, the waveform within five. Each zoom runs on an empty cache of
/// its own, in a temporary folder deleted afterwards, so nothing measured was made by an earlier
/// zoom or an earlier run. Asking is done the way the timeline asks: the visible stretch at
/// visible priority, the whole clip behind it at idle priority, the waveform at visible priority,
/// all at once, then polled every few milliseconds until each is complete.
/// </remarks>
public static class ThumbnailBenchmark
{
    /// <summary>The width of the timeline the visible stretch is measured on.</summary>
    public const double ScreenWidth = 1500;

    /// <summary>How wide a thumbnail is drawn on a default height video track.</summary>
    public const double TileWidth = 87;

    /// <summary>Runs the benchmark.</summary>
    /// <param name="path">The file.</param>
    /// <param name="zooms">Pixels per second to measure at; the fit zoom and a working one when empty.</param>
    public static ThumbnailBenchmarkResult Run(string path, IReadOnlyList<double>? zooms = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string root = Directory.CreateTempSubdirectory("jazz-perf-thumbs-").FullName;
        try
        {
            var clock = Stopwatch.StartNew();
            MediaItem item;
            using (var cache = new CacheManager(Path.Combine(root, "import")))
            {
                item = new MediaImporter(cache).Import(new ImportSource(Path.GetFullPath(path), MediaKind.Movie)).Item;
            }

            double import = clock.Elapsed.TotalMilliseconds;

            CacheSource video = CacheSource.Video(item, string.Empty)
                ?? throw new InvalidOperationException($"'{path}' has no picture to take thumbnails of.");

            double seconds = item.Duration.ToSeconds();
            double fit = ScreenWidth / (Math.Max(seconds, 1.0) * 1.05);
            IReadOnlyList<double> measured = zooms is { Count: > 0 } ? zooms : [fit, 40.0];

            var results = new List<ThumbnailZoomResult>();
            double waveform = -1;
            double warm = 0;

            for (int index = 0; index < measured.Count; index++)
            {
                string folder = Path.Combine(root, index.ToString(CultureInfo.InvariantCulture));
                using var cache = new CacheManager(folder);
                using var thumbnails = new ThumbnailService(cache);
                using var waveforms = new WaveformService(cache);

                bool withSound = index == 0 && item.Info?.AudioStreams.FirstOrDefault() is not null;
                CacheSource? sound = withSound ? CacheSource.Audio(item, string.Empty, item.Info!.AudioStreams.First().Index) : null;

                (ThumbnailZoomResult zoom, double wave) = Measure(thumbnails, waveforms, video, sound, measured[index], import);
                results.Add(zoom);

                if (sound is not null)
                {
                    waveform = wave;
                }

                if (index == measured.Count - 1)
                {
                    warm = Warm(cache, video, measured[index]);
                }
            }

            MediaStream? picture = item.Info?.VideoStreams.FirstOrDefault();

            return new ThumbnailBenchmarkResult(
                Path.GetFileName(path),
                seconds,
                picture is null ? string.Empty : $"{picture.Width}x{picture.Height} {picture.Codec} {picture.FrameRate?.ToDisplayString()} fps",
                WorkQueue.DefaultWorkers,
                import,
                results,
                waveform,
                warm);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A worker still closing a file; the temporary folder is the system's to clean.
            }
        }
    }

    /// <summary>Human readable lines.</summary>
    public static string Describe(ThumbnailBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{result.File}: {result.Duration:F0} s, {result.Picture}"),
            string.Create(CultureInfo.InvariantCulture, $"  import (hash and probe)  {result.ImportMilliseconds,8:F0} ms"),
        };

        foreach (ThumbnailZoomResult zoom in result.Zooms)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  {zoom.PixelsPerSecond,6:F1} px/s, every {zoom.Spacing:0.#} s: {zoom.Visible,3} visible in {zoom.VisibleMilliseconds,6:F0} ms, {zoom.Strip,4} shown in {zoom.StripMilliseconds,7:F0} ms, exact in {zoom.ExactMilliseconds,7:F0} ms"));
        }

        lines.Add(result.WaveformMilliseconds < 0
            ? "  waveform                 no sound"
            : string.Create(CultureInfo.InvariantCulture, $"  waveform                 {result.WaveformMilliseconds,8:F0} ms"));
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"  strip again, from disk   {result.WarmStripMilliseconds,8:F0} ms"));
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {result.Workers} workers; times include the import"));

        return string.Join(Environment.NewLine, lines);
    }

    private static (ThumbnailZoomResult Zoom, double Waveform) Measure(
        ThumbnailService thumbnails,
        WaveformService waveforms,
        CacheSource video,
        CacheSource? sound,
        double pixelsPerSecond,
        double import)
    {
        Flicks spacing = ThumbnailService.SpacingFor(pixelsPerSecond, TileWidth);
        Flicks screen = Flicks.Min(Flicks.FromSeconds(ScreenWidth / pixelsPerSecond), video.Duration);
        int visible = Count(spacing, screen);
        int strip = Count(spacing, video.Duration);

        var clock = Stopwatch.StartNew();
        double visibleAt = -1;
        double stripAt = -1;
        double waveAt = sound is null ? 0 : -1;
        double exactAt = -1;

        thumbnails.Strip(video, spacing, Flicks.Zero, video.Duration, WorkPriority.Idle);

        while (visibleAt < 0 || stripAt < 0 || waveAt < 0 || exactAt < 0)
        {
            // Asked again on every poll, as the timeline asks on every frame it draws.
            if (visibleAt < 0 && (thumbnails.Strip(video, spacing, Flicks.Zero, screen, WorkPriority.Visible).Count >= visible || Idle(thumbnails)))
            {
                visibleAt = clock.Elapsed.TotalMilliseconds;
            }

            if (stripAt < 0 && (thumbnails.Strip(video, spacing, Flicks.Zero, video.Duration, WorkPriority.Idle).Count >= strip || Idle(thumbnails)))
            {
                stripAt = clock.Elapsed.TotalMilliseconds;
            }

            if (exactAt < 0 && stripAt >= 0 && visibleAt >= 0 && Idle(thumbnails))
            {
                exactAt = clock.Elapsed.TotalMilliseconds;
            }

            if (waveAt < 0 && waveforms.Get(sound!, WorkPriority.Visible) is { IsComplete: true })
            {
                waveAt = clock.Elapsed.TotalMilliseconds;
            }

            if (clock.Elapsed > TimeSpan.FromMinutes(10))
            {
                throw new InvalidOperationException("Thumbnails were not all made in ten minutes.");
            }

            Thread.Sleep(5);
        }

        return (
            new ThumbnailZoomResult(pixelsPerSecond, spacing.ToSeconds(), visible, strip, import + visibleAt, import + stripAt, import + exactAt),
            sound is null ? -1 : import + waveAt);
    }

    /// <summary>The strip again in a new service over the same cache: memory empty, the disk full.</summary>
    private static double Warm(CacheManager cache, CacheSource video, double pixelsPerSecond)
    {
        using var thumbnails = new ThumbnailService(cache);
        Flicks spacing = ThumbnailService.SpacingFor(pixelsPerSecond, TileWidth);
        int strip = Count(spacing, video.Duration);

        var clock = Stopwatch.StartNew();
        while (thumbnails.Strip(video, spacing, Flicks.Zero, video.Duration, WorkPriority.Visible).Count < strip && !Idle(thumbnails))
        {
            Thread.Sleep(2);
        }

        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Nothing waiting and nothing running: done, even if a time or two past the real end of the
    /// stream (the probe can overstate it by a frame) never produced a picture.
    /// </summary>
    private static bool Idle(ThumbnailService thumbnails) => thumbnails.Work.Pending == 0 && thumbnails.Work.Running == 0;

    private static int Count(Flicks spacing, Flicks length) =>
        (int)((length.Value - 1) / spacing.Value) + 1;
}
