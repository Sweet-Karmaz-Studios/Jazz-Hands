using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;
using JazzHands.Media.Probe;

namespace JazzHands.Cli;

/// <summary>What "jazz perf decode" reports.</summary>
/// <param name="File">The file that was decoded.</param>
/// <param name="Codec">The codec short name.</param>
/// <param name="Size">Coded resolution.</param>
/// <param name="FrameRate">The stream frame rate.</param>
/// <param name="Path">Hardware or software.</param>
/// <param name="Decoder">Which FFmpeg decoder was selected.</param>
/// <param name="Frames">Frames decoded per pass.</param>
/// <param name="Passes">How many times the file was decoded.</param>
/// <param name="Seconds">Total decode time.</param>
/// <param name="Fps">Frames per second, which is the number that matters.</param>
/// <param name="RealtimeFactor">How many times faster than playback that is.</param>
/// <param name="BytesPerFrame">Managed bytes allocated per frame in steady state.</param>
/// <param name="PooledFrameShells">Frame shells the pool allocated. Flat is the point.</param>
/// <param name="PeakWorkingSetMb">Peak working set during the run.</param>
/// <param name="VramUsedMb">Video memory this process holds at the end of the run.</param>
/// <param name="VramGrowthMb">Video memory growth across passes, which is the real leak check.</param>
/// <param name="WorkingSetGrowthMb">Working set growth across passes, which is the leak check.</param>
public sealed record DecodeBenchmarkResult(
    string File,
    string Codec,
    string Size,
    string FrameRate,
    string Path,
    string Decoder,
    long Frames,
    int Passes,
    double Seconds,
    double Fps,
    double RealtimeFactor,
    double BytesPerFrame,
    int PooledFrameShells,
    double PeakWorkingSetMb,
    double WorkingSetGrowthMb,
    double VramUsedMb,
    double VramGrowthMb)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The human-readable form.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{System.IO.Path.GetFileName(File)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  codec      {Codec} {Size} @ {FrameRate}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  decoder    {Decoder} ({Path.ToLowerInvariant()})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  frames     {Frames} x {Passes} pass{(Passes == 1 ? string.Empty : "es")} in {Seconds:F2} s");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  throughput {Fps:F1} fps ({RealtimeFactor:F1}x realtime)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  allocation {BytesPerFrame:F1} bytes/frame, {PooledFrameShells} pooled shells");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  memory     {PeakWorkingSetMb:F0} MB peak, {WorkingSetGrowthMb:+0.0;-0.0;0.0} MB growth across passes");
        sb.Append(CultureInfo.InvariantCulture, $"  vram       {VramUsedMb:F0} MB used, {VramGrowthMb:+0.0;-0.0;0.0} MB growth across passes");
        return sb.ToString();
    }

    /// <summary>The --json form.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>
/// Decodes a file as fast as it can and reports throughput, allocation and memory growth.
/// </summary>
/// <remarks>
/// This is the harness behind spike S2 and the decode half of Docs/PERF.md. It deliberately does
/// no rendering: it measures the decoder and nothing else, so a regression here is unambiguous.
/// </remarks>
public static class DecodeBenchmark
{
    /// <summary>Runs the benchmark.</summary>
    /// <param name="path">The media file.</param>
    /// <param name="useHardware">Try D3D11VA. False forces the software path.</param>
    /// <param name="passes">How many times to decode the file. More than one checks for leaks.</param>
    /// <param name="reuseDecoder">Keep one decoder across passes, the way playback loops do.</param>
    /// <param name="cancellationToken">Cancels between frames.</param>
    public static DecodeBenchmarkResult Run(
        string path,
        bool useHardware = true,
        int passes = 1,
        bool reuseDecoder = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);

        using HardwareDeviceContext? hardware = useHardware
            ? HardwareDeviceContext.TryCreateStandalone()
            : null;

        var probe = new Prober().Probe(path, detectFrameRateMode: false);
        StreamInfo video = probe.VideoStreams.FirstOrDefault()
            ?? throw new InvalidOperationException($"'{path}' has no video stream.");

        using var process = Process.GetCurrentProcess();

        // Working set says little about a decoder whose surfaces live in VRAM, so ask the adapter.
        using var vram = new VideoMemoryProbe();

        // One warm-up pass: decoder setup, JIT and the first surface allocations are not what we
        // are measuring.
        long framesPerPass = DecodeOnce(path, hardware, cancellationToken, out DecodePath decodePath, out string decoder, out int pooled);

        process.Refresh();
        long workingSetAfterWarmup = process.WorkingSet64;
        long vramAfterWarmup = vram.UsedBytes();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        long totalFrames = 0;

        if (reuseDecoder)
        {
            // One decoder, rewound between passes. This is what playing a clip on a loop looks
            // like, and it separates a decode leak from the cost of churning decoders.
            totalFrames = DecodeRepeatedly(path, hardware, passes, cancellationToken, out decodePath, out decoder, out pooled);
        }
        else
        {
            for (int pass = 0; pass < passes; pass++)
            {
                totalFrames += DecodeOnce(path, hardware, cancellationToken, out decodePath, out decoder, out pooled);
            }
        }

        timer.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        process.Refresh();
        double growthMb = (process.WorkingSet64 - workingSetAfterWarmup) / 1024.0 / 1024.0;
        long vramNow = vram.UsedBytes();

        Rational frameRate = video.Video!.FrameRate;
        double seconds = timer.Elapsed.TotalSeconds;
        double fps = totalFrames / seconds;

        return new DecodeBenchmarkResult(
            System.IO.Path.GetFullPath(path),
            video.CodecName,
            $"{video.Video.Width}x{video.Video.Height}",
            frameRate.ToString(),
            decodePath.ToString(),
            decoder,
            framesPerPass,
            passes,
            Math.Round(seconds, 3),
            Math.Round(fps, 1),
            Math.Round(fps / frameRate.ToDouble(), 1),
            totalFrames == 0 ? 0 : Math.Round((double)allocated / totalFrames, 1),
            pooled,
            Math.Round(process.PeakWorkingSet64 / 1024.0 / 1024.0, 1),
            Math.Round(growthMb, 1),
            Math.Round(vramNow / 1024.0 / 1024.0, 1),
            Math.Round((vramNow - vramAfterWarmup) / 1024.0 / 1024.0, 1));
    }

    private static long DecodeRepeatedly(
        string path,
        HardwareDeviceContext? hardware,
        int passes,
        CancellationToken cancellationToken,
        out DecodePath decodePath,
        out string decoderName,
        out int pooledShells)
    {
        using var demuxer = new Demuxer(path);
        int stream = demuxer.FindBestStream(StreamKind.Video);
        if (stream < 0)
        {
            throw new InvalidOperationException($"{path} has no video stream.");
        }

        using var decoder = new VideoDecoder(demuxer, stream, hardware);

        long frames = 0;
        for (int pass = 0; pass < passes; pass++)
        {
            while (decoder.ReadFrame() is { } frame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                frame.Dispose();
                frames++;
            }

            demuxer.Rewind();
            decoder.Flush();
        }

        decodePath = decoder.Path;
        decoderName = decoder.CodecName;
        pooledShells = decoder.PooledFrames;
        return frames;
    }

    private static long DecodeOnce(
        string path,
        HardwareDeviceContext? hardware,
        CancellationToken cancellationToken,
        out DecodePath decodePath,
        out string decoderName,
        out int pooledShells)
    {
        using var demuxer = new Demuxer(path);
        int stream = demuxer.FindBestStream(StreamKind.Video);
        if (stream < 0)
        {
            throw new InvalidOperationException($"'{path}' has no video stream.");
        }

        using var decoder = new VideoDecoder(demuxer, stream, hardware);

        long frames = 0;
        while (decoder.ReadFrame() is { } frame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frame.Dispose();
            frames++;
        }

        decodePath = decoder.Path;
        decoderName = decoder.CodecName;
        pooledShells = decoder.PooledFrames;
        return frames;
    }
}
