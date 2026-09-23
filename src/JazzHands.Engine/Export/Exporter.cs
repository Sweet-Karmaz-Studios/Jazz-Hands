using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Media.Encode;
using Serilog;

namespace JazzHands.Engine.Export;

/// <summary>
/// Runs an export plan: a stream copy, an encode in process, or an encode through ffmpeg.exe.
/// </summary>
/// <remarks>
/// The file is written beside the output under a temporary name and moved into place only when
/// it is complete, so a failed or cancelled export never leaves half a file where a whole one is
/// expected, and never destroys an older file of the same name before the new one exists.
///
/// An encode renders on the calling thread and encodes on a writer thread, joined by a bounded
/// queue of four frames: rendering, reading back and encoding overlap, and a slow encoder holds
/// the renderer back rather than filling memory. The writer owns the muxer, both encoders and the
/// sound, so nothing FFmpeg holds is touched by two threads.
/// </remarks>
public static class Exporter
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(Exporter));

    /// <summary>Runs a plan to completion.</summary>
    /// <param name="plan">What to write.</param>
    /// <param name="project">The project the plan was made from.</param>
    /// <param name="projectPath">Where it lives, for relative media paths.</param>
    /// <param name="environment">Which device renders.</param>
    /// <param name="progress">Told how far it has got, on the exporting threads; may be null.</param>
    /// <param name="cancellationToken">Stops it. Nothing is left behind.</param>
    public static ExportResult Run(
        ExportPlan plan,
        Project project,
        string projectPath,
        ExportEnvironment? environment = null,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(project);

        environment ??= ExportEnvironment.Default;
        string folder = Path.GetDirectoryName(plan.OutputPath) ?? ".";
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(
            folder,
            $".{Path.GetFileNameWithoutExtension(plan.OutputPath)}.{Guid.NewGuid():N}.partial{Path.GetExtension(plan.OutputPath)}");

        try
        {
            ExportResult result = plan.Mode switch
            {
                ExportMode.Copy => Copy(plan, temporary, progress, cancellationToken),
                _ when plan.External => ExternalFfmpegExporter.Run(plan, project, projectPath, temporary, environment, progress, cancellationToken),
                _ => Encode(plan, project, projectPath, temporary, environment, progress, cancellationToken),
            };

            File.Move(temporary, plan.OutputPath, overwrite: true);
            Log.Information(
                "Exported {Path}: {Mode} with {Encoder}, {Bytes} bytes, {Duration} in {Seconds:F1} s ({Speed:F1}x real time)",
                plan.OutputPath,
                plan.Mode,
                result.Encoder,
                result.Bytes,
                Timecode.FormatClock(result.Duration),
                result.Elapsed.TotalSeconds,
                result.Speed);

            return result with { Path = plan.OutputPath };
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static ExportResult Copy(ExportPlan plan, string temporary, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        ExportCopy copy = plan.Copy ?? throw new ArgumentException("A copy plan says what to copy.", nameof(plan));

        var job = new StreamCopyJob(
            temporary,
            [.. copy.SourceRanges.Select(range => new CopySegment(copy.SourcePath, range.Start, range.End))],
            copy.VideoStream,
            [.. copy.AudioStreams],
            copy.FrameRate,
            plan.Container,
            FastStart: plan.Container is "mp4" or "mov");

        IProgress<CopyProgress>? relay = progress is null
            ? null
            : new Synchronous<CopyProgress>(step => progress.Report(new ExportProgress(step.Fraction, 0, 0, 0, step.Bytes, "copy")));

        StreamCopyResult copied = StreamCopier.Copy(job, relay, cancellationToken);
        return new ExportResult(temporary, copied.Bytes, copied.Duration, "copy", 0, copied.Elapsed, []);
    }

    private static ExportResult Encode(
        ExportPlan plan,
        Project project,
        string projectPath,
        string temporary,
        ExportEnvironment environment,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        long total = ExportRenderer.CountFrames(plan);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var renderer = new ExportRenderer(environment);
        using var writer = new EncodeWriter(plan, project, projectPath, temporary, total, progress, linked);

        try
        {
            writer.WaitUntilReady(linked.Token);
            renderer.Render(project, projectPath, plan, writer, linked.Token);
            writer.Complete();
        }
        catch
        {
            linked.Cancel();
            writer.Abandon();

            // The writer's own error is the one worth reporting: a render it stopped only says
            // it was cancelled.
            writer.ThrowIfFailed();
            throw;
        }

        writer.ThrowIfFailed();

        return new ExportResult(
            temporary,
            new FileInfo(temporary).Length,
            plan.Duration,
            writer.EncoderName ?? "unknown",
            total,
            clock.Elapsed,
            writer.Notes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException error)
        {
            Log.Warning(error, "Could not delete the partial export {Path}", path);
        }
        catch (UnauthorizedAccessException error)
        {
            Log.Warning(error, "Could not delete the partial export {Path}", path);
        }
    }

    /// <summary>Reports on the thread that made the report, rather than posting it somewhere.</summary>
    internal sealed class Synchronous<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>The writer thread: encoders, sound and muxer, fed frames by the renderer.</summary>
    private sealed class EncodeWriter : IFrameSink, IDisposable
    {
        private const int FrameCount = 6;
        private const int QueueLength = 4;

        private readonly ExportPlan _plan;
        private readonly Project _project;
        private readonly string _projectPath;
        private readonly string _path;
        private readonly long _total;
        private readonly IProgress<ExportProgress>? _progress;
        private readonly CancellationTokenSource _cancellation;
        private readonly BlockingCollection<EncoderFrame> _free = new(FrameCount);
        private readonly BlockingCollection<(EncoderFrame Frame, long Index)> _pending = new(QueueLength);
        private readonly List<EncoderFrame> _frames = [];
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private readonly List<string> _notes = [];
        private Exception? _failure;

        public EncodeWriter(
            ExportPlan plan,
            Project project,
            string projectPath,
            string path,
            long total,
            IProgress<ExportProgress>? progress,
            CancellationTokenSource cancellation)
        {
            _plan = plan;
            _project = project;
            _projectPath = projectPath;
            _path = path;
            _total = total;
            _progress = progress;
            _cancellation = cancellation;
            _thread = new Thread(Run) { IsBackground = true, Name = "Jazz export writer" };
            _thread.Start();
        }

        public string? EncoderName { get; private set; }

        public IReadOnlyList<string> Notes => _notes;

        public void WaitUntilReady(CancellationToken cancellationToken)
        {
            WaitHandle.WaitAny([_ready.WaitHandle, cancellationToken.WaitHandle]);
            ThrowIfFailed();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public EncoderFrame Rent(CancellationToken cancellationToken)
        {
            try
            {
                return _free.Take(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ThrowIfFailed();
                throw;
            }
        }

        public void Submit(EncoderFrame frame, long index, CancellationToken cancellationToken)
        {
            try
            {
                _pending.Add((frame, index), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ThrowIfFailed();
                throw;
            }
        }

        /// <summary>No more frames: flush, write the trailer and wait for the thread.</summary>
        public void Complete()
        {
            _pending.CompleteAdding();
            _thread.Join();
        }

        /// <summary>Stops without finishing the file.</summary>
        public void Abandon()
        {
            if (!_pending.IsAddingCompleted)
            {
                _pending.CompleteAdding();
            }

            _thread.Join();
        }

        public void ThrowIfFailed()
        {
            if (_failure is { } failure)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        public void Dispose()
        {
            if (_thread.IsAlive)
            {
                Abandon();
            }

            // Only once both threads are done with them: the renderer may be filling one while
            // the writer is already failing.
            foreach (EncoderFrame frame in _frames)
            {
                frame.Dispose();
            }

            _free.Dispose();
            _pending.Dispose();
            _ready.Dispose();
        }

        private void Run()
        {
            ExportVideo video = _plan.Video!;
            Muxer? muxer = null;
            VideoEncoder? encoder = null;
            AudioEncoder? sound = null;
            ExportSound? mix = null;

            try
            {
                muxer = Muxer.Create(_path, _plan.Container);
                encoder = VideoEncoder.Open(Settings(video), muxer.NeedsGlobalHeader);
                EncoderName = encoder.Name;
                _notes.AddRange(encoder.Skipped.Select(skipped => $"Skipped {skipped}."));

                if (_plan.Audio is { } audio)
                {
                    sound = AudioEncoder.Open(
                        new AudioEncoderSettings(audio.Encoder, audio.SampleRate, audio.Channels, audio.Bitrate),
                        muxer.NeedsGlobalHeader);
                    Sequence sequence = _project.Sequence(_plan.SequenceId)!;
                    mix = new ExportSound(_project, sequence, _projectPath, [.. _plan.Ranges], audio.SampleRate, audio.Channels);
                }

                int videoStream = muxer.AddStream(encoder);
                int soundStream = sound is null ? -1 : muxer.AddStream(sound);
                muxer.WriteHeader(fastStart: _plan.Container is "mp4" or "mov");

                for (int index = 0; index < FrameCount; index++)
                {
                    EncoderFrame frame = encoder.CreateFrame();
                    _frames.Add(frame);
                    _free.Add(frame);
                }

                _ready.Set();

                var clock = Stopwatch.StartNew();
                long written = 0;
                long reported = 0;

                foreach ((EncoderFrame frame, long index) in _pending.GetConsumingEnumerable())
                {
                    _cancellation.Token.ThrowIfCancellationRequested();

                    encoder.Encode(frame, index, muxer, videoStream);
                    _free.Add(frame);
                    written = index + 1;

                    mix?.WriteUpTo(SamplesAt(written), sound!, muxer, soundStream);

                    long now = clock.ElapsedMilliseconds;
                    if (_progress is not null && now - reported >= 250)
                    {
                        reported = now;
                        double fps = written / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                        _progress.Report(new ExportProgress((double)written / Math.Max(1, _total), written, _total, fps, muxer.BytesWritten, encoder.Name));
                    }
                }

                _cancellation.Token.ThrowIfCancellationRequested();
                if (written < _total)
                {
                    throw new OperationCanceledException("The export stopped before its last frame.");
                }

                encoder.Flush(muxer, videoStream);
                if (mix is not null)
                {
                    mix.WriteUpTo(mix.Total, sound!, muxer, soundStream);
                    sound!.Flush(muxer, soundStream);
                }

                muxer.Finish();
                _progress?.Report(new ExportProgress(
                    1.0,
                    written,
                    _total,
                    written / Math.Max(0.001, clock.Elapsed.TotalSeconds),
                    muxer.BytesWritten,
                    encoder.Name));
            }
            catch (Exception error)
            {
                // Anything at all: this is the top of a thread, and the renderer rethrows it.
                _failure = error;
                _ready.Set();

                // Stops a renderer waiting for a free frame or a place in the queue.
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            finally
            {
                mix?.Dispose();
                sound?.Dispose();
                encoder?.Dispose();
                muxer?.Dispose();
            }
        }

        private long SamplesAt(long frames) =>
            Flicks.FromFrames(frames, _plan.Video!.FrameRate).ToTimebase(1, _plan.Audio!.SampleRate, RoundingMode.Nearest);

        private static VideoEncoderSettings Settings(ExportVideo video) => new(
            [.. video.Encoders],
            video.Width,
            video.Height,
            video.FrameRate,
            video.Quality,
            video.Bitrate,
            video.Speed switch
            {
                "fast" => EncoderSpeed.Fast,
                "slow" => EncoderSpeed.Slow,
                _ => EncoderSpeed.Medium,
            },
            video.GopLength,
            video.BFrames,
            video.Lossless);

        internal static VideoEncoderSettings SettingsFor(ExportVideo video) => Settings(video);
    }

    /// <summary>The encoder settings a plan's video side means, for the ffmpeg.exe path to match.</summary>
    internal static VideoEncoderSettings EncoderSettings(ExportVideo video) => EncodeWriter.SettingsFor(video);
}
