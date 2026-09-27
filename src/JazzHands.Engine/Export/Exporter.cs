using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using JazzHands.Core;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Time;
using JazzHands.Media.Encode;
using JazzHands.Media.SmartCut;
using Serilog;

namespace JazzHands.Engine.Export;

/// <summary>
/// Runs an export plan: a stream copy, an encode in process, or an encode through ffmpeg.exe.
/// </summary>
/// <remarks>
/// <para>
/// The file is written beside the output under a temporary name and moved into place only when
/// it is complete, so a failed or cancelled export never leaves half a file where a whole one is
/// expected, and never destroys an older file of the same name before the new one exists. An image
/// sequence is written into a temporary folder beside its frames and moved frame by frame.
/// </para>
/// <para>
/// An encode renders on the calling thread and encodes on a writer thread, joined by a bounded
/// queue of four frames: rendering, reading back and encoding overlap, and a slow encoder holds
/// the renderer back rather than filling memory. The writer owns the muxer, both encoders and the
/// sound, so nothing FFmpeg holds is touched by two threads. A sound-only export has no renderer
/// and mixes straight into the encoder.
/// </para>
/// <para>
/// A plan with a size target is checked when it is done. A file over the target is encoded again
/// at a bitrate scaled by how far over it came, less five percent, up to three times; the second
/// try almost always lands, since a rate controlled encoder misses by a steady proportion on the
/// same pictures.
/// </para>
/// </remarks>
public static class Exporter
{
    private const int SizeRetries = 3;

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
        DiskSpace.Check(plan);
        bool sequence = ExportPresets.Container(plan.Container) is { Sequence: true };

        // An image sequence goes into a folder of its own and its frames move out of it; a file
        // gets a hidden name beside where it is going.
        string scratch = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(plan.OutputPath).Replace("%", string.Empty, StringComparison.Ordinal)}.{Guid.NewGuid():N}.partial");
        string temporary = sequence
            ? Path.Combine(scratch, Path.GetFileName(plan.OutputPath))
            : scratch + Path.GetExtension(plan.OutputPath);

        try
        {
            if (sequence)
            {
                Directory.CreateDirectory(scratch);
            }

            ExportResult result = Attempt(plan, project, projectPath, temporary, environment, progress, cancellationToken);
            var notes = new List<string>(result.Notes);

            for (int attempt = 1; plan.TargetBytes > 0 && result.Bytes > plan.TargetBytes && plan.Video is { Bitrate: > 0 } video; attempt++)
            {
                if (attempt > SizeRetries)
                {
                    throw new ExportException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The file came to {ExportPresets.FormatBytes(result.Bytes)}, over {ExportPresets.FormatBytes(plan.TargetBytes)}, after {SizeRetries} tries. Export a shorter stretch or a smaller size."));
                }

                long bitrate = (long)(video.Bitrate * ((double)plan.TargetBytes / result.Bytes) * 0.95);
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Came to {ExportPresets.FormatBytes(result.Bytes)}, over {ExportPresets.FormatBytes(plan.TargetBytes)}; encoded again at {bitrate / 1000} kb/s."));
                Log.Information("Export {Path} is {Bytes} bytes, over its {Target} byte target; encoding again at {Bitrate} b/s", plan.OutputPath, result.Bytes, plan.TargetBytes, bitrate);

                TryDelete(temporary);
                plan = plan with { Video = video with { Bitrate = bitrate } };
                ExportResult again = Attempt(plan, project, projectPath, temporary, environment, progress, cancellationToken);
                result = again with { Elapsed = result.Elapsed + again.Elapsed };
            }

            result = result with { Notes = [.. notes, .. result.Notes.Except(notes)] };

            if (sequence)
            {
                MoveFrames(scratch, folder);
            }
            else
            {
                File.Move(temporary, plan.OutputPath, overwrite: true);
            }

            WriteSidecars(plan, project);
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
        catch (Exception error) when (DiskSpace.IsFull(error))
        {
            Log.Warning(error, "The disk filled up exporting {Path}", plan.OutputPath);
            throw DiskSpace.Full(plan.OutputPath, error);
        }
        catch (Exception error) when (Render.RenderDevice.IsDeviceLoss(error))
        {
            Log.Warning(error, "The GPU was reset exporting {Path}", plan.OutputPath);
            throw new ExportException(
                "The graphics card was reset while exporting (a driver update or crash, or it was disabled). The part-written file was removed and nothing else was touched; export again.",
                error);
        }
        finally
        {
            if (sequence)
            {
                TryDeleteFolder(scratch);
            }
            else
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>
    /// The subtitle streams and chapters a plan puts in its file: each soft subtitle track's cues
    /// at their times in the output, and the plan's chapters. Null when there are none.
    /// </summary>
    internal static MuxExtras? Extras(ExportPlan plan, Project project)
    {
        Sequence? sequence = project.Sequence(plan.SequenceId);
        MuxSubtitleTrack[] subtitles = plan.Subtitles is { Delivery: SubtitleDelivery.Soft } soft && sequence is not null
            ? [.. soft.Tracks
                .Select(track => (Plan: track, Track: sequence.Tracks.FirstOrDefault(candidate => candidate.Id == track.TrackId)))
                .Where(pair => pair.Track is not null && pair.Plan.Codec is not null)
                .Select(pair => new MuxSubtitleTrack(pair.Plan.Codec!, SubtitleTracks.Document(pair.Track!, plan.Ranges), pair.Plan.Language, pair.Plan.Name, pair.Plan.Default))]
            : [];
        MuxChapter[] chapters = plan.Chapters.IsEmpty ? [] : [.. plan.Chapters.Select(chapter => new MuxChapter(chapter.Start, chapter.End, chapter.Title))];

        return subtitles.Length == 0 && chapters.Length == 0 ? null : new MuxExtras(subtitles, chapters);
    }

    /// <summary>The bytes an export wrote: one file's size, or every frame of an image sequence.</summary>
    internal static long SizeOf(string path)
    {
        if (!path.Contains('%', StringComparison.Ordinal))
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }

        string folder = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileName(path);
        string pattern = name[..name.IndexOf('%', StringComparison.Ordinal)] + "*" + Path.GetExtension(name);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern).Sum(file => new FileInfo(file).Length) : 0;
    }

    /// <summary>Makes the file once: a copy, a sound-only encode, an encode through ffmpeg.exe, or one in process.</summary>
    private static ExportResult Attempt(
        ExportPlan plan,
        Project project,
        string projectPath,
        string temporary,
        ExportEnvironment environment,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken) => plan.Mode switch
        {
            ExportMode.Copy => Copy(plan, project, temporary, progress, cancellationToken),
            ExportMode.Smart => Smart(plan, project, temporary, progress, cancellationToken),
            _ when plan.Video is null => EncodeSound(plan, project, projectPath, temporary, progress, cancellationToken),
            _ when plan.External => ExternalFfmpegExporter.Run(plan, project, projectPath, temporary, environment, progress, cancellationToken),
            _ => Encode(plan, project, projectPath, temporary, environment, progress, cancellationToken),
        };

    /// <summary>Writes the subtitle files a plan puts beside its video, at their times in the output.</summary>
    private static void WriteSidecars(ExportPlan plan, Project project)
    {
        if (plan.Subtitles is not { Delivery: SubtitleDelivery.Sidecar } sidecar || project.Sequence(plan.SequenceId) is not { } sequence)
        {
            return;
        }

        foreach (ExportSubtitleTrack planned in sidecar.Tracks)
        {
            if (planned.SidecarPath is not { } path || sequence.Tracks.FirstOrDefault(track => track.Id == planned.TrackId) is not { } track)
            {
                continue;
            }

            string text = SubtitleFiles.Write(SubtitleTracks.Document(track, plan.Ranges), sidecar.SidecarFormat);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Log.Information("Wrote subtitles for {Track} to {Path}", track.Name, path);
        }
    }

    /// <summary>
    /// The gain that brings a plan's mix to its loudness target, measured by playing the whole mix
    /// through a meter first, and what was done; 1 and no note for a plan without a target.
    /// </summary>
    internal static float Loudness(ExportPlan plan, Project project, string projectPath, List<string> notes, CancellationToken cancellationToken)
    {
        if (plan.Audio is not { Loudness: { } target } audio)
        {
            return 1.0f;
        }

        Sequence sequence = project.Sequence(plan.SequenceId)!;
        (float integrated, float peak) = ExportSound.Measure(project, sequence, projectPath, [.. plan.Ranges], audio.SampleRate, audio.Channels, cancellationToken);
        float gain = ExportSound.GainFor(target, integrated, peak, out string note);
        notes.Add(note);
        Log.Information("Loudness: {Note}", note);
        return gain;
    }

    private static ExportResult Copy(ExportPlan plan, Project project, string temporary, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        ExportCopy copy = plan.Copy ?? throw new ArgumentException("A copy plan says what to copy.", nameof(plan));
        using MuxExtras? extras = Extras(plan, project);

        var job = new StreamCopyJob(
            temporary,
            [.. copy.SourceRanges.Select(range => new CopySegment(copy.SourcePath, range.Start, range.End))],
            copy.VideoStream,
            [.. copy.AudioStreams],
            copy.FrameRate,
            plan.Container,
            FastStart: plan.Container is "mp4" or "mov",
            Extras: extras);

        IProgress<CopyProgress>? relay = progress is null
            ? null
            : new Synchronous<CopyProgress>(step => progress.Report(new ExportProgress(step.Fraction, 0, 0, 0, step.Bytes, "copy")));

        StreamCopyResult copied = StreamCopier.Copy(job, relay, cancellationToken);
        return new ExportResult(temporary, copied.Bytes, copied.Duration, "copy", 0, copied.Elapsed, []);
    }

    /// <summary>
    /// A smart cut: the source's packets between the cuts, the frames around them encoded again
    /// with a matched encoder, then the file checked packet by packet before it is kept.
    /// </summary>
    private static ExportResult Smart(ExportPlan plan, Project project, string temporary, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        ExportSmart smart = plan.Smart ?? throw new ArgumentException("A smart cut plan says what to copy and what to encode.", nameof(plan));
        (MatchSource? source, string? reason) = SmartCutter.Describe(smart.SourcePath, smart.VideoStream, smart.GopFrames);
        if (source is null)
        {
            throw new ExportException($"'{smart.SourcePath}' can no longer be smart cut: {reason}");
        }

        using MuxExtras? extras = Extras(plan, project);
        var job = new SmartCutJob(
            temporary,
            smart.SourcePath,
            smart.VideoStream,
            [.. smart.Segments.Select(segment => new SmartSegment(segment.Start, segment.End, segment.Encode, segment.From))],
            [.. smart.AudioStreams],
            [.. smart.SourceRanges],
            smart.FrameRate,
            [.. smart.Encoders],
            plan.Container,
            FastStart: plan.Container is "mp4" or "mov",
            Extras: extras);

        IProgress<CopyProgress>? relay = progress is null
            ? null
            : new Synchronous<CopyProgress>(step => progress.Report(new ExportProgress(step.Fraction, 0, 0, 0, step.Bytes, "smart")));

        SmartCutResult result = SmartCutter.Run(job, source, relay, cancellationToken);
        long frames = smart.EncodedFrames + smart.CopiedFrames;
        IReadOnlyList<string> problems = SmartCutVerifier.Check(temporary, source, frames, smart.AudioStreams.Length, cancellationToken: cancellationToken);
        if (problems.Count > 0)
        {
            throw new ExportException($"The smart cut did not check out: {string.Join(" ", problems)} Export with --mode encode instead.");
        }

        var notes = new List<string>(result.Notes)
        {
            string.Create(CultureInfo.InvariantCulture, $"{Words.Count(result.EncodedFrames, "frame")} encoded again with {result.Encoder ?? "nothing"}, {result.CopiedPackets} copied; every frame checked in place."),
        };

        return new ExportResult(temporary, result.Bytes, result.Duration, $"smart ({result.Encoder ?? "copy"})", result.EncodedFrames, result.Elapsed, notes);
    }

    /// <summary>A sound-only export: the mix straight into the encoder, with no picture to render.</summary>
    private static ExportResult EncodeSound(
        ExportPlan plan,
        Project project,
        string projectPath,
        string temporary,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ExportAudio audio = plan.Audio ?? throw new ArgumentException("A sound-only plan has a sound side.", nameof(plan));
        var clock = Stopwatch.StartNew();
        var notes = new List<string>();
        float gain = Loudness(plan, project, projectPath, notes, cancellationToken);

        using Muxer muxer = Muxer.Create(temporary, plan.Container);
        using AudioEncoder sound = AudioEncoder.Open(new AudioEncoderSettings(audio.Encoder, audio.SampleRate, audio.Channels, audio.Bitrate), muxer.NeedsGlobalHeader);
        using var mix = new ExportSound(project, project.Sequence(plan.SequenceId)!, projectPath, [.. plan.Ranges], audio.SampleRate, audio.Channels) { Gain = gain };
        using MuxExtras? extras = Extras(plan, project);

        int stream = muxer.AddStream(sound);
        extras?.Open(muxer);
        muxer.WriteHeader(fastStart: plan.Container is "mp4" or "mov");

        long step = audio.SampleRate;
        long reported = 0;
        for (long written = 0; written < mix.Total; written = Math.Min(mix.Total, written + step))
        {
            cancellationToken.ThrowIfCancellationRequested();
            mix.WriteUpTo(written + step, sound, muxer, stream);
            extras?.WriteUpTo(muxer, Flicks.FromTimebase(Math.Min(mix.Total, written + step), new Rational(1, audio.SampleRate)));

            long now = clock.ElapsedMilliseconds;
            if (progress is not null && now - reported >= 250)
            {
                reported = now;
                progress.Report(new ExportProgress((double)written / Math.Max(1, mix.Total), 0, 0, 0, muxer.BytesWritten, audio.Encoder));
            }
        }

        sound.Flush(muxer, stream);
        extras?.Close(muxer);
        muxer.Finish();
        progress?.Report(new ExportProgress(1.0, 0, 0, 0, muxer.BytesWritten, audio.Encoder));

        return new ExportResult(temporary, SizeOf(temporary), plan.Duration, audio.Encoder, 0, clock.Elapsed, notes);
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
        using var writer = new EncodeWriter(plan, project, projectPath, temporary, total, progress, linked, renderer.Textures);

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
            SizeOf(temporary),
            plan.Duration,
            writer.EncoderName ?? "unknown",
            total,
            clock.Elapsed,
            writer.Notes);
    }

    /// <summary>Moves an image sequence's frames out of the folder they were written into.</summary>
    private static void MoveFrames(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Move(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException error)
        {
            Log.Warning(error, "Could not delete the partial export folder {Path}", path);
        }
        catch (UnauthorizedAccessException error)
        {
            Log.Warning(error, "Could not delete the partial export folder {Path}", path);
        }
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
        private readonly BlockingCollection<(EncoderFrame? Frame, TextureFrame? Texture, long Index)> _pending = new(QueueLength);
        private readonly D3D11Textures? _textures;
        private readonly Lock _encoderGate = new();
        private VideoEncoder? _encoder;
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
            CancellationTokenSource cancellation,
            D3D11Textures? textures)
        {
            _plan = plan;
            _textures = textures;
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

        public bool TenBit { get; private set; }

        public bool Rgba { get; private set; }

        public bool TakesTextures { get; private set; }

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
                _pending.Add((frame, null, index), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ThrowIfFailed();
                throw;
            }
        }

        public TextureFrame RentTexture(CancellationToken cancellationToken)
        {
            // Under the gate the writer disposes the encoder behind, so a failing writer cannot
            // free the pool while the renderer is taking a texture from it.
            lock (_encoderGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (_encoder ?? throw new OperationCanceledException("The encoder has closed.")).RentTexture();
            }
        }

        public void Submit(TextureFrame frame, long index, CancellationToken cancellationToken)
        {
            try
            {
                _pending.Add((null, frame, index), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                frame.Dispose();
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

            // Textures the writer never reached go back to their pool.
            while (_pending.TryTake(out (EncoderFrame? Frame, TextureFrame? Texture, long Index) left))
            {
                left.Texture?.Dispose();
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
            MuxExtras? extras = null;

            try
            {
                muxer = Muxer.Create(_path, _plan.Container);
                encoder = VideoEncoder.Open(Settings(video), muxer.NeedsGlobalHeader, _textures);
                lock (_encoderGate)
                {
                    _encoder = encoder;
                }

                EncoderName = encoder.Name;
                TenBit = encoder.TakesP010;
                Rgba = encoder.TakesRgba;
                TakesTextures = encoder.TakesTextures;
                _notes.AddRange(encoder.Skipped.Select(skipped => $"Skipped {skipped}."));

                if (_plan.Audio is { } audio)
                {
                    sound = AudioEncoder.Open(
                        new AudioEncoderSettings(audio.Encoder, audio.SampleRate, audio.Channels, audio.Bitrate),
                        muxer.NeedsGlobalHeader);
                    Sequence sequence = _project.Sequence(_plan.SequenceId)!;
                    float gain = Loudness(_plan, _project, _projectPath, _notes, _cancellation.Token);
                    mix = new ExportSound(_project, sequence, _projectPath, [.. _plan.Ranges], audio.SampleRate, audio.Channels) { Gain = gain };
                }

                int videoStream = muxer.AddStream(encoder);
                int soundStream = sound is null ? -1 : muxer.AddStream(sound);
                extras = Extras(_plan, _project);
                extras?.Open(muxer);
                muxer.WriteHeader(fastStart: _plan.Container is "mp4" or "mov");

                for (int index = 0; index < FrameCount && !encoder.TakesTextures; index++)
                {
                    EncoderFrame frame = encoder.CreateFrame();
                    _frames.Add(frame);
                    _free.Add(frame);
                }

                _ready.Set();

                var clock = Stopwatch.StartNew();
                long written = 0;
                long reported = 0;

                foreach ((EncoderFrame? frame, TextureFrame? texture, long index) in _pending.GetConsumingEnumerable())
                {
                    if (texture is not null)
                    {
                        // NVENC takes a reference of its own; this one goes back now.
                        using (texture)
                        {
                            _cancellation.Token.ThrowIfCancellationRequested();
                            encoder.Encode(texture, index, muxer, videoStream);
                        }
                    }
                    else
                    {
                        _cancellation.Token.ThrowIfCancellationRequested();
                        encoder.Encode(frame!, index, muxer, videoStream);
                        _free.Add(frame!);
                    }

                    written = index + 1;

                    mix?.WriteUpTo(SamplesAt(written), sound!, muxer, soundStream);
                    extras?.WriteUpTo(muxer, Flicks.FromFrames(written, video.FrameRate));

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

                extras?.Close(muxer);
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
                extras?.Dispose();
                mix?.Dispose();
                sound?.Dispose();
                lock (_encoderGate)
                {
                    encoder?.Dispose();
                    _encoder = null;
                }

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
            video.Lossless,
            video.PixelFormat,
            video.Profile,
            video.Level);

        internal static VideoEncoderSettings SettingsFor(ExportVideo video) => Settings(video);
    }

    /// <summary>The encoder settings a plan's video side means, for the ffmpeg.exe path to match.</summary>
    internal static VideoEncoderSettings EncoderSettings(ExportVideo video) => EncodeWriter.SettingsFor(video);
}
