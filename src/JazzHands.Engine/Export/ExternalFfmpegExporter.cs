using System.Diagnostics;
using System.Globalization;
using System.Text;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Media;
using JazzHands.Media.Encode;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Engine.Export;

/// <summary>
/// Encodes through ffmpeg.exe instead of in process, from the same plan and the same frames.
/// </summary>
/// <remarks>
/// It exists to tell an encoder problem from ours. The frames are rendered exactly as the
/// in-process path renders them and piped to ffmpeg.exe's standard input as raw NV12; the sound
/// is mixed first into a raw float file beside the output, because two pipes into one process
/// that reads them at its own pace is a deadlock waiting for a long export. The encoder and its
/// options are the ones <see cref="VideoEncoder.EncoderOptions"/> gives the in-process encoder,
/// so a difference between the two files is a difference in how the frames were delivered, not
/// in what was asked for.
///
/// This is the one place Jazz Hands runs ffmpeg.exe (CLAUDE.md). Stream copy has no encoder to
/// bisect, so it never comes here.
/// </remarks>
internal static class ExternalFfmpegExporter
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(ExternalFfmpegExporter));

    public static ExportResult Run(
        ExportPlan plan,
        Project project,
        string projectPath,
        string temporary,
        ExportEnvironment environment,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ExportVideo video = plan.Video ?? throw new ArgumentException("An encode plan has a video side.", nameof(plan));
        FfmpegLoader.Initialize();

        string executable = Path.Combine(FfmpegLoader.BinaryDirectory, "ffmpeg.exe");
        if (!File.Exists(executable))
        {
            throw new FfmpegException($"ffmpeg.exe is not beside the FFmpeg libraries in '{FfmpegLoader.BinaryDirectory}'.");
        }

        var clock = Stopwatch.StartNew();
        VideoEncoderSettings settings = Exporter.EncoderSettings(video);
        string encoder = ChooseEncoder(settings, out List<string> notes);
        string? soundFile = null;

        try
        {
            if (plan.Audio is { } audio)
            {
                soundFile = temporary + ".f32";
                MixToFile(project, projectPath, plan, audio, soundFile, cancellationToken);
            }

            string arguments = Arguments(plan, video, settings, encoder, soundFile, temporary);
            Log.Information("Encoding through ffmpeg.exe: {Arguments}", arguments);

            long total = ExportRenderer.CountFrames(plan);
            RenderInto(executable, arguments, project, projectPath, plan, environment, total, encoder, progress, cancellationToken);

            notes.Add("Encoded by ffmpeg.exe.");
            return new ExportResult(temporary, new FileInfo(temporary).Length, plan.Duration, encoder, total, clock.Elapsed, notes);
        }
        finally
        {
            if (soundFile is not null && File.Exists(soundFile))
            {
                File.Delete(soundFile);
            }
        }
    }

    /// <summary>The first encoder in the chain that opens here, which is the one ffmpeg.exe will be able to open too.</summary>
    private static string ChooseEncoder(VideoEncoderSettings settings, out List<string> notes)
    {
        using VideoEncoder probe = VideoEncoder.Open(settings, globalHeader: false);
        notes = [.. probe.Skipped.Select(skipped => $"Skipped {skipped}.")];
        return probe.Name;
    }

    private static void MixToFile(Project project, string projectPath, ExportPlan plan, ExportAudio audio, string path, CancellationToken cancellationToken)
    {
        Sequence sequence = project.Sequence(plan.SequenceId)!;
        using var mix = new ExportSound(project, sequence, projectPath, [.. plan.Ranges], audio.SampleRate, audio.Channels);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        byte[] interleaved = new byte[4096 * audio.Channels * sizeof(float)];

        int count;
        while ((count = mix.Read(4096)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<float> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(interleaved.AsSpan());
            for (int sample = 0; sample < count; sample++)
            {
                for (int channel = 0; channel < audio.Channels; channel++)
                {
                    samples[(sample * audio.Channels) + channel] = mix.Planes[channel][sample];
                }
            }

            file.Write(interleaved, 0, count * audio.Channels * sizeof(float));
        }
    }

    private static string Arguments(ExportPlan plan, ExportVideo video, VideoEncoderSettings settings, string encoder, string? soundFile, string output)
    {
        var arguments = new StringBuilder();
        void Add(params string[] parts)
        {
            foreach (string part in parts)
            {
                arguments.Append(' ').Append(Quote(part));
            }
        }

        Add("-hide_banner", "-loglevel", "error", "-nostdin", "-y");
        Add("-f", "rawvideo", "-pix_fmt", "nv12", "-s", $"{video.Width}x{video.Height}", "-framerate", video.FrameRate.ToString(), "-i", "pipe:0");

        if (soundFile is not null)
        {
            ExportAudio audio = plan.Audio!;
            Add("-f", "f32le", "-ar", Invariant(audio.SampleRate), "-ac", Invariant(audio.Channels), "-i", soundFile);
        }

        Add("-map", "0:v");
        if (soundFile is not null)
        {
            Add("-map", "1:a");
        }

        Add("-c:v", encoder);
        foreach ((string key, string value) in VideoEncoder.EncoderOptions(encoder, settings))
        {
            Add($"-{key}", value);
        }

        Add("-g", Invariant(settings.GopLength), "-bf", Invariant(settings.BFrames));
        if (settings.Bitrate > 0 && !settings.Lossless)
        {
            Add("-b:v", Invariant(settings.Bitrate));
        }

        Add("-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv");
        if (encoder.StartsWith("libx265", StringComparison.Ordinal))
        {
            Add("-pix_fmt", "yuv420p");
        }

        if (soundFile is not null)
        {
            ExportAudio audio = plan.Audio!;
            Add("-c:a", audio.Encoder);
            if (audio.Encoder != "flac")
            {
                Add("-b:a", Invariant(audio.Bitrate));
            }
        }

        Add("-fflags", "+bitexact");
        if (plan.Container is "mp4" or "mov")
        {
            Add("-movflags", "+faststart");
        }

        Add("-f", plan.Container, output);
        return arguments.ToString().TrimStart();
    }

    private static void RenderInto(
        string executable,
        string arguments,
        Project project,
        string projectPath,
        ExportPlan plan,
        ExportEnvironment environment,
        long total,
        string encoder,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(start) ?? throw new FfmpegException("ffmpeg.exe would not start.");
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { Length: > 0 } text)
            {
                lock (errors)
                {
                    errors.AppendLine(text);
                }
            }
        };
        process.BeginErrorReadLine();

        using CancellationTokenRegistration kill = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        try
        {
            using var renderer = new ExportRenderer(environment);
            using var sink = new PipeSink(process.StandardInput.BaseStream, plan.Video!.Width, plan.Video.Height, total, encoder, progress);
            renderer.Render(project, projectPath, plan, sink, cancellationToken);
        }
        catch (IOException error) when (!cancellationToken.IsCancellationRequested)
        {
            // The pipe breaks when ffmpeg.exe gives up; what it said is the useful part.
            process.WaitForExit();
            throw new FfmpegException($"ffmpeg.exe stopped: {Said(errors)}", error);
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }

        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            throw new FfmpegException($"ffmpeg.exe exited with {process.ExitCode}: {Said(errors)}");
        }
    }

    private static string Said(StringBuilder errors)
    {
        lock (errors)
        {
            string text = errors.ToString().Trim();
            return text.Length == 0 ? "it said nothing." : text;
        }
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string part) =>
        part.Contains(' ', StringComparison.Ordinal) || part.Contains('"', StringComparison.Ordinal)
            ? "\"" + part.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : part;

    /// <summary>Writes each frame's planes to ffmpeg.exe's standard input as they come.</summary>
    private sealed class PipeSink(Stream pipe, int width, int height, long total, string encoder, IProgress<ExportProgress>? progress)
        : IFrameSink, IDisposable
    {
        private readonly EncoderFrame _frame = EncoderFrame.CreateNv12(width, height);
        private readonly byte[] _row = new byte[width];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _reported;

        public EncoderFrame Rent(CancellationToken cancellationToken) => _frame;

        public unsafe void Submit(EncoderFrame frame, long index, CancellationToken cancellationToken)
        {
            WritePlane((byte*)frame.Plane(0), frame.Stride(0), height);
            WritePlane((byte*)frame.Plane(1), frame.Stride(1), height / 2);

            long now = _clock.ElapsedMilliseconds;
            if (progress is not null && now - _reported >= 250)
            {
                _reported = now;
                long written = index + 1;
                progress.Report(new ExportProgress(
                    (double)written / Math.Max(1, total),
                    written,
                    total,
                    written / Math.Max(0.001, _clock.Elapsed.TotalSeconds),
                    0,
                    encoder));
            }
        }

        public void Dispose() => _frame.Dispose();

        private unsafe void WritePlane(byte* plane, int stride, int rows)
        {
            for (int row = 0; row < rows; row++)
            {
                new ReadOnlySpan<byte>(plane + ((long)row * stride), width).CopyTo(_row);
                pipe.Write(_row, 0, width);
            }
        }
    }
}
