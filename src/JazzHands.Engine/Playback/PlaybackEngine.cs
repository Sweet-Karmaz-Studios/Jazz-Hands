using System.Diagnostics;
using JazzHands.Core.Commands;
using JazzHands.Core.Diagnostics;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Diagnostics;
using JazzHands.Engine.Frames;
using JazzHands.Media.Decode;
using JazzHands.Media.Import;
using JazzHands.Render;
using JazzHands.Render.Color;
using JazzHands.Render.Frames;
using JazzHands.Render.Passes;
using Serilog;
using Vortice.Direct3D11;

namespace JazzHands.Engine.Playback;

/// <summary>
/// Plays a sequence: pictures on the preview in step with the sound the transport is making.
/// </summary>
/// <remarks>
/// The transport's clock is the master. It counts what the sound card has actually played, so
/// the picture follows the sound rather than the other way round, and a sound card whose crystal
/// runs a little fast drags the picture with it instead of drifting away from it. This class owns
/// the composition thread, which wakes when a frame boundary is due, works out which sequence
/// frame the clock is in, renders that one and hands it to every <see cref="IPreviewTarget"/>.
///
/// The drop policy is the simple one: always show the frame the clock is in now. A frame that
/// was due while the thread was busy is never shown late; it is counted as dropped and skipped.
/// At normal speed that count is the health of playback, and the phase bar is under 0.1% over
/// five minutes of 4K60. At other rates skipping is the point and nothing is counted.
///
/// Decoding happens on this thread too, because the decoders, the frame cache and the device
/// context are thread affine and a second thread would mean a second copy of each. The frame
/// server decodes a few frames past whatever it was asked for, and after each present the time
/// left before the next frame is due is spent decoding further ahead, so the frame that is due
/// is almost always already there.
///
/// Until the compositor arrives in Phase 10 this shows one layer: the topmost enabled clip on a
/// visible video track, fitted to the frame the way its media asks.
/// </remarks>
public sealed class PlaybackEngine : IPlaybackController, IDisposable
{
    private static readonly double TicksPerMillisecond = Stopwatch.Frequency / 1000.0;

    private readonly ILogger _log = Log.ForContext<PlaybackEngine>();
    private readonly Transport _transport;
    private readonly RenderDevice _device;
    private readonly PlaybackOptions _options;
    private readonly DiagnosticsLog? _notices;
    private readonly CacheManager? _cacheManager;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _targetsGate = new();
    private readonly Lock _controlGate = new();
    private readonly Dictionary<(string Hash, int Stream), KeyframeIndex?> _keyframes = [];
    private readonly Dictionary<string, string> _failedMedia = new(StringComparer.Ordinal);

    private IPreviewTarget[] _targets = [];
    private volatile bool _disposing;
    private Session? _session;

    // What to play. Written by any thread, read by the composition thread.
    private volatile ProjectSnapshot _snapshot = new(Project.CreateNew("Untitled"), string.Empty, 0);

    // Settings, written by the control side.
    private volatile bool _loop;
    private PreviewQuality _quality = PreviewQuality.Auto;
    private long _lastSeekTicks;
    private long _scrubUntilTicks;
    private long _seekGeneration;
    private volatile bool _refresh;

    // The composition thread's own state.
    private RenderKey _lastKey;
    private long _lastPlayingFrame = -1;
    private long _lastPlayingGeneration = -1;
    private long _lastEventTicks;
    private long _lastEventFrame = -1;
    private (TransportState State, double Rate) _lastReported = (TransportState.Stopped, 1.0);
    private ID3D11Texture2D? _program;
    private ID3D11RenderTargetView? _programView;
    private int _programWidth;
    private int _programHeight;
    private PreviewFrame? _lastFrame;

    private long _presented;
    private long _dropped;
    private long _rendered;
    private long _frameOnScreen;
    private int _effectiveQuality = (int)PreviewQuality.Full;

    /// <summary>Creates the engine and starts its composition thread. Plays nothing until asked.</summary>
    /// <param name="transport">The audio side and the master clock. Not owned.</param>
    /// <param name="device">The device frames are decoded, rendered and presented on. Not owned.</param>
    /// <param name="options">How to set up; defaults suit the editor.</param>
    /// <param name="notices">Where decoder fallbacks and missing media are reported.</param>
    /// <param name="cacheManager">Where keyframe indexes are kept between runs, for reverse play.</param>
    public PlaybackEngine(
        Transport transport,
        RenderDevice device,
        PlaybackOptions? options = null,
        DiagnosticsLog? notices = null,
        CacheManager? cacheManager = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(device);

        _transport = transport;
        _device = device;
        _options = options ?? new PlaybackOptions();
        _notices = notices;
        _cacheManager = cacheManager;

        _thread = new Thread(CompositionLoop)
        {
            IsBackground = true,
            Name = "Jazz composition",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>
    /// Raised on the composition thread when the frame on screen changes or playback changes
    /// state. At most every 33 ms while playing, so a timeline or a remote client following it
    /// is not flooded; every change while paused, because each one is somebody's deliberate step.
    /// </summary>
    public event EventHandler<PlayheadMovedEventArgs>? PlayheadMoved;

    /// <summary>The transport this engine plays through.</summary>
    public Transport Transport => _transport;

    /// <inheritdoc />
    public Flicks Position => _transport.Playhead;

    /// <summary>Frames handed to the preview targets.</summary>
    public long PresentedFrames => Interlocked.Read(ref _presented);

    /// <summary>Frames rendered, including ones re-rendered at a new quality.</summary>
    public long RenderedFrames => Interlocked.Read(ref _rendered);

    /// <summary>Frames that were due at normal speed and never shown.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>The sequence frame last put on screen.</summary>
    public long FrameOnScreen => Interlocked.Read(ref _frameOnScreen);

    /// <summary>What Auto quality last resolved to, or the quality asked for.</summary>
    public PreviewQuality EffectiveQuality => (PreviewQuality)Volatile.Read(ref _effectiveQuality);

    /// <inheritdoc />
    public bool Loop
    {
        get => _loop;
        set
        {
            _loop = value;
            _wake.Set();
        }
    }

    /// <inheritdoc />
    public PreviewQuality Quality
    {
        get
        {
            lock (_controlGate)
            {
                return _quality;
            }
        }

        set
        {
            lock (_controlGate)
            {
                _quality = value;
            }

            _wake.Set();
        }
    }

    /// <summary>Plays a project from now on. Any thread.</summary>
    public void Load(Project project, string projectPath = "")
    {
        ArgumentNullException.ThrowIfNull(project);

        ProjectSnapshot previous = _snapshot;
        _snapshot = new ProjectSnapshot(project, projectPath, previous.Version + 1);
        _wake.Set();
    }

    /// <summary>Follows a session: plays its project now and again after every command.</summary>
    public void Attach(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Detach();
        _session = session;
        session.ProjectChanged += OnProjectChanged;
        Load(session.Project, session.ProjectPath);
    }

    /// <summary>Stops following the session.</summary>
    public void Detach()
    {
        if (_session is { } session)
        {
            session.ProjectChanged -= OnProjectChanged;
            _session = null;
        }
    }

    /// <summary>Adds somewhere frames are shown. The current frame is presented to it straight away.</summary>
    public void AddTarget(IPreviewTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_targetsGate)
        {
            _targets = [.. _targets, target];
        }

        Refresh();
    }

    /// <summary>Stops showing frames somewhere.</summary>
    public void RemoveTarget(IPreviewTarget target)
    {
        lock (_targetsGate)
        {
            _targets = [.. _targets.Where(existing => !ReferenceEquals(existing, target))];
        }
    }

    /// <summary>
    /// Presents the current frame again, for a target that has resized, zoomed or come back
    /// from a lost device. Nothing is decoded or rendered again.
    /// </summary>
    public void Refresh()
    {
        _refresh = true;
        _wake.Set();
    }

    /// <inheritdoc />
    public void Play()
    {
        lock (_controlGate)
        {
            Sequence? sequence = _snapshot.Project.ActiveSequence;
            if (sequence is not null && _transport.State != TransportState.Playing)
            {
                // Playing from the end, or from outside a loop, starts from the beginning of the
                // range, which is what pressing play on a finished sequence means.
                (Flicks start, Flicks end) = PlayRange(sequence, _loop);
                Flicks here = _transport.Playhead;
                Flicks last = end - _snapshot.Project.SettingsFor(sequence).FrameDuration;

                if (here >= last || (_loop && here < start))
                {
                    _transport.Seek(start);
                    Interlocked.Increment(ref _seekGeneration);
                }
            }

            if (_transport.Rate != 1.0)
            {
                _transport.Rate = 1.0;
            }

            _transport.Play();
        }

        _wake.Set();
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_controlGate)
        {
            _transport.Pause();
        }

        _wake.Set();
    }

    /// <inheritdoc />
    public void Toggle()
    {
        if (_transport.State == TransportState.Playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_controlGate)
        {
            _transport.Stop();
            if (_transport.Rate != 1.0)
            {
                _transport.Rate = 1.0;
            }

            Interlocked.Increment(ref _seekGeneration);
        }

        _wake.Set();
    }

    /// <inheritdoc />
    public void Seek(Flicks time)
    {
        time = Flicks.Max(Flicks.Zero, time);

        lock (_controlGate)
        {
            bool playing = _transport.State == TransportState.Playing;
            long now = Stopwatch.GetTimestamp();

            if (!playing)
            {
                // Two seeks close together while stopped are somebody dragging the playhead,
                // which is when Auto quality drops to Half and audio plays a grain per move.
                if (now - _lastSeekTicks < Ticks(_options.ScrubWindow))
                {
                    _scrubUntilTicks = now + Ticks(_options.ScrubSettle);
                }

                _lastSeekTicks = now;
            }

            _transport.Seek(time);
            Interlocked.Increment(ref _seekGeneration);

            if (!playing)
            {
                _transport.Scrub(time);
            }
        }

        _wake.Set();
    }

    /// <inheritdoc />
    public void Step(int frames)
    {
        lock (_controlGate)
        {
            if (_transport.State == TransportState.Playing)
            {
                _transport.Pause();
            }

            Sequence? sequence = _snapshot.Project.ActiveSequence;
            if (sequence is null)
            {
                return;
            }

            Rational rate = _snapshot.Project.SettingsFor(sequence).FrameRate;
            long current = _transport.Playhead.ToFrames(rate, RoundingMode.Floor);
            long last = Math.Max(0, sequence.Duration.ToFrames(rate, RoundingMode.Ceiling) - 1);
            long target = Math.Clamp(current + frames, 0, last);

            _transport.Seek(Flicks.FromFrames(target, rate));
            Interlocked.Increment(ref _seekGeneration);
        }

        _wake.Set();
    }

    /// <inheritdoc />
    public void Shuttle(double rate)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate))
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "A shuttle rate has to be a finite number.");
        }

        if (rate == 0)
        {
            Pause();
            return;
        }

        lock (_controlGate)
        {
            _transport.Rate = rate;

            if (_transport.State != TransportState.Playing)
            {
                _transport.Play();
            }

            Interlocked.Increment(ref _seekGeneration);
        }

        _wake.Set();
    }

    /// <inheritdoc />
    public PlaybackStateInfo Describe()
    {
        ProjectSnapshot snapshot = _snapshot;
        Sequence? sequence = snapshot.Project.ActiveSequence;
        Rational fps = sequence is null ? snapshot.Project.Settings.FrameRate : snapshot.Project.SettingsFor(sequence).FrameRate;
        Flicks position = _transport.Playhead;

        return new PlaybackStateInfo(
            _transport.State.ToString().ToLowerInvariant(),
            sequence?.Id,
            position,
            Timecode.Format(position, fps),
            position.ToFrames(fps, RoundingMode.Floor),
            _transport.Rate,
            _loop,
            Quality,
            EffectiveQuality,
            sequence?.InOut?.Start,
            sequence?.InOut?.End,
            PresentedFrames,
            DroppedFrames,
            _transport.CanStretch);
    }

    /// <summary>Waits until the engine has presented a frame after this call, for tests and scripts.</summary>
    public bool WaitForPresent(TimeSpan timeout)
    {
        long before = PresentedFrames;
        Refresh();
        return SpinWait.SpinUntil(() => PresentedFrames > before, timeout);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposing)
        {
            return;
        }

        Detach();
        _disposing = true;
        _wake.Set();
        _thread.Join();
        _wake.Dispose();
    }

    /// <summary>The sequence frame the playhead is in, as the composition thread will render it.</summary>
    internal static long FrameAt(Flicks time, Rational rate) => Math.Max(0, time.ToFrames(rate, RoundingMode.Floor));

    /// <summary>The range playback runs over: the in and out points when looping over them, else the sequence.</summary>
    internal static (Flicks Start, Flicks End) PlayRange(Sequence sequence, bool loop)
    {
        if (loop && sequence.InOut is { } range && range.Duration > Flicks.Zero)
        {
            return (range.Start, range.End);
        }

        return (Flicks.Zero, sequence.Duration);
    }

    /// <summary>The topmost enabled clip on a visible video track at a time, which is what one layer shows.</summary>
    internal static Clip? TopClip(Sequence sequence, Flicks time)
    {
        Clip? top = null;
        int order = int.MinValue;

        foreach (Track track in sequence.Tracks)
        {
            // A muted video track is a hidden one.
            if (track.Kind != TrackKind.Video || track.Muted || track.Order < order)
            {
                continue;
            }

            if (Core.Queries.TimelineQueries.ClipAt(track, time) is { Enabled: true } clip)
            {
                top = clip;
                order = track.Order;
            }
        }

        return top;
    }

    /// <summary>Where a source picture goes in the frame, by its media's conform policy.</summary>
    internal static QuadRect Placement(
        ConformPolicy policy,
        int pictureWidth,
        int pictureHeight,
        int sequenceWidth,
        int sequenceHeight,
        int targetWidth,
        int targetHeight)
    {
        switch (policy)
        {
            case ConformPolicy.Stretch:
                return QuadRect.Full;

            case ConformPolicy.Native:
                // One source pixel to one sequence pixel, whatever the working resolution is.
                return QuadRect.Zoom(pictureWidth, pictureHeight, targetWidth, targetHeight, (double)targetWidth / sequenceWidth);

            case ConformPolicy.Fill:
                double cover = Math.Max((double)targetWidth / pictureWidth, (double)targetHeight / pictureHeight);
                return QuadRect.Zoom(pictureWidth, pictureHeight, targetWidth, targetHeight, cover);

            default:
                return QuadRect.Fit(pictureWidth, pictureHeight, targetWidth, targetHeight);
        }
    }

    /// <summary>The colour signalling of a clip's picture, from what import recorded.</summary>
    /// <remarks>
    /// The probe records whether a stream is HDR but not its matrix, so the rest is the convention
    /// every player falls back on: BT.2020 with PQ for HDR, BT.601 for standard definition and
    /// BT.709 for everything else, limited range throughout.
    /// </remarks>
    internal static YuvColorSpace ColorSpaceFor(MediaItem item, int streamIndex, PixelLayout layout)
    {
        MediaStream? stream = item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == streamIndex);

        if (stream?.IsHdr == true)
        {
            return YuvColorSpace.From("bt2020nc", "smpte2084", isFullRange: false, layout.BitDepth);
        }

        string matrix = stream is { Height: > 0 and <= 576 } ? "bt601" : "bt709";
        return YuvColorSpace.From(matrix, "bt709", isFullRange: false, layout.BitDepth);
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalMilliseconds * TicksPerMillisecond);

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (_session is { } session)
        {
            Load(e.Project, session.ProjectPath);
        }
    }

    /// <summary>What the composition thread does, from start to shutdown.</summary>
    /// <remarks>
    /// Everything thread affine is made here and disposed here: the hardware device context, the
    /// decoder pool, the frame cache and the pass.
    /// </remarks>
    private void CompositionLoop()
    {
        HardwareDeviceContext? hardware = null;
        SourceFrameServer? server = null;
        PreviewPass? pass = null;
        TimerResolution? resolution = null;

        try
        {
            _device.EnableMultithreadProtection();

            if (_options.HardwareDecode && _device.SupportsVideo)
            {
                hardware = HardwareDeviceContext.CreateShared(_device.Device.NativePointer, _device.ImmediateContext.NativePointer);
            }

            server = new SourceFrameServer(
                new DecoderPool(hardware),
                new FrameCache(new FrameTexturePool(_device), _options.FrameCacheBytes),
                _device,
                _notices);
            pass = new PreviewPass(_device);

            while (!_disposing)
            {
                bool playing = _transport.State == TransportState.Playing;

                // The timer resolution is raised only while something is moving.
                if (playing && resolution is null)
                {
                    resolution = new TimerResolution();
                }
                else if (!playing && resolution is not null)
                {
                    resolution.Dispose();
                    resolution = null;
                }

                int wait = Tick(server, pass);
                if (wait > 0)
                {
                    _wake.WaitOne(wait);
                }
            }
        }
        catch (Exception exception)
        {
            _log.Error(exception, "The composition thread stopped; the preview will not update until the editor restarts");
        }
        finally
        {
            resolution?.Dispose();
            ReleaseProgram();
            pass?.Dispose();
            server?.Dispose();
            hardware?.Dispose();
        }
    }

    /// <summary>
    /// One turn of the loop: settle the range, render the frame the clock is in if it is not the
    /// one already shown, decode ahead, and say how long to sleep.
    /// </summary>
    /// <returns>Milliseconds until something is next due.</returns>
    private int Tick(SourceFrameServer server, PreviewPass pass)
    {
        ProjectSnapshot snapshot = _snapshot;
        Project project = snapshot.Project;
        Sequence? sequence = project.ActiveSequence;
        long now = Stopwatch.GetTimestamp();

        if (sequence is null)
        {
            return 50;
        }

        ProjectSettings settings = project.SettingsFor(sequence);
        Rational fps = settings.FrameRate;
        Flicks frameLength = settings.FrameDuration;

        TransportState state = _transport.State;
        bool playing = state == TransportState.Playing;
        double rate = playing ? _transport.Rate : 0.0;
        Flicks time = _transport.Playhead;

        if (playing)
        {
            time = KeepInRange(sequence, time, rate, frameLength);
            playing = _transport.State == TransportState.Playing;
            state = _transport.State;
        }

        long frame = FrameAt(time, fps);
        PreviewQuality effective = Resolve(now, playing, rate);
        Volatile.Write(ref _effectiveQuality, (int)effective);

        long generation = Interlocked.Read(ref _seekGeneration);
        var key = new RenderKey(snapshot.Version, frame, effective);
        bool refresh = _refresh;
        _refresh = false;

        if (key != _lastKey || _lastFrame is null)
        {
            CountDrops(frame, playing, rate, generation);
            Render(server, pass, snapshot, sequence, settings, frame, time, effective, playing, rate);
            _lastKey = key;
            Present();
        }
        else if (refresh)
        {
            Present();
        }

        Report(time, frame, state, playing ? rate : _transport.Rate, now);

        if (playing)
        {
            DecodeAhead(server, snapshot, sequence, fps, frame, rate, frameLength);
            return MillisecondsToNextFrame(time, frame, fps, rate);
        }

        // Paused: sleep until woken, or until a scrub settles and Auto wants to go back to Full.
        long settle = Volatile.Read(ref _scrubUntilTicks) - Stopwatch.GetTimestamp();
        return settle > 0 ? Math.Max(1, (int)(settle / TicksPerMillisecond) + 1) : 50;
    }

    /// <summary>Loops or stops at the edges of the range, returning where the playhead now is.</summary>
    private Flicks KeepInRange(Sequence sequence, Flicks time, double rate, Flicks frameLength)
    {
        bool loop = _loop;
        (Flicks start, Flicks end) = PlayRange(sequence, loop);
        Flicks last = Flicks.Max(start, end - frameLength);

        lock (_controlGate)
        {
            if (rate > 0 && time >= end)
            {
                if (loop && end > start)
                {
                    _transport.Seek(start);
                    Interlocked.Increment(ref _seekGeneration);
                    return start;
                }

                _transport.Pause();
                _transport.Seek(last);
                return last;
            }

            if (rate < 0 && time <= start)
            {
                if (loop && end > start)
                {
                    _transport.Seek(last);
                    Interlocked.Increment(ref _seekGeneration);
                    return last;
                }

                _transport.Pause();
                _transport.Seek(start);
                return start;
            }
        }

        return time;
    }

    private PreviewQuality Resolve(long now, bool playing, double rate)
    {
        PreviewQuality quality = Quality;
        if (quality != PreviewQuality.Auto)
        {
            return quality;
        }

        bool shuttling = playing && Math.Abs(rate) > 1.0;
        bool scrubbing = now < Volatile.Read(ref _scrubUntilTicks);
        return shuttling || scrubbing ? PreviewQuality.Half : PreviewQuality.Full;
    }

    /// <summary>
    /// Counts the frames that were due and never shown. Only at normal speed forwards, and only
    /// between two frames of the same uninterrupted run: a seek, a loop or a start is not a drop.
    /// </summary>
    private void CountDrops(long frame, bool playing, double rate, long generation)
    {
        if (!playing || rate != 1.0)
        {
            _lastPlayingFrame = -1;
            return;
        }

        if (_lastPlayingFrame >= 0 && generation == _lastPlayingGeneration && frame > _lastPlayingFrame + 1)
        {
            long missed = frame - _lastPlayingFrame - 1;
            Interlocked.Add(ref _dropped, missed);
        }

        _lastPlayingFrame = frame;
        _lastPlayingGeneration = generation;
    }

    private void Render(
        SourceFrameServer server,
        PreviewPass pass,
        ProjectSnapshot snapshot,
        Sequence sequence,
        ProjectSettings settings,
        long frame,
        Flicks playhead,
        PreviewQuality quality,
        bool playing,
        double rate)
    {
        int divisor = quality switch
        {
            PreviewQuality.Half => 2,
            PreviewQuality.Quarter => 4,
            _ => 1,
        };

        int width = Math.Max(1, (settings.Width + divisor - 1) / divisor);
        int height = Math.Max(1, (settings.Height + divisor - 1) / divisor);
        EnsureProgram(width, height);

        pass.Clear(_programView!);

        Flicks time = Flicks.FromFrames(frame, settings.FrameRate);
        Project project = snapshot.Project;

        if (TopClip(sequence, time) is { } clip && clip.MediaId is { } mediaId && project.MediaItem(mediaId) is { } item
            && !_failedMedia.ContainsKey(item.Id))
        {
            try
            {
                FrameTexture? source = Fetch(server, snapshot, clip, item, time, playing, rate);

                if (source is not null)
                {
                    QuadRect placement = Placement(
                        item.Conform,
                        source.Width,
                        source.Height,
                        settings.Width,
                        settings.Height,
                        width,
                        height);

                    pass.DrawFrame(source, ColorSpaceFor(item, clip.SourceStreamIndex, source.Layout), _programView!, width, height, placement);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One broken file must not take the preview down with it. It is reported once and
                // left black until the project changes, rather than retried at sixty a second.
                _failedMedia[item.Id] = exception.Message;
                _log.Error(exception, "Could not show {Media} in the preview", item.Name);
                _notices?.Report(
                    item.Id,
                    item.Name,
                    DiagnosticCodes.DecodeFailed,
                    $"The preview could not show this file: {exception.Message}",
                    DiagnosticLevel.Error);
            }
        }

        Interlocked.Increment(ref _rendered);
        Interlocked.Exchange(ref _frameOnScreen, frame);
        _lastFrame = new PreviewFrame(_program!, width, height, settings.Width, settings.Height, frame, time, playhead, quality);
    }

    /// <summary>Gets a clip's picture the way the current rate needs it.</summary>
    private FrameTexture? Fetch(
        SourceFrameServer server,
        ProjectSnapshot snapshot,
        Clip clip,
        MediaItem item,
        Flicks time,
        bool playing,
        double rate)
    {
        // Past twice normal speed a shuttle shows keyframes: every frame would mean decoding
        // sixty times faster than real time, and nobody can see the difference at 8x.
        SeekMode mode = playing && Math.Abs(rate) > 2.0 ? SeekMode.Nearest : SeekMode.Exact;
        PlayDirection direction = !playing ? PlayDirection.Still : (rate > 0) != clip.Reverse ? PlayDirection.Forward : PlayDirection.Reverse;

        if (direction == PlayDirection.Reverse && mode == SeekMode.Exact)
        {
            // Backwards through the source: decode the group the frame is in forwards and keep
            // all of it, which is the only way there is. The next frames are then cache hits.
            FrameTexture? cached = server.GetCachedFrame(snapshot.Project, clip, time);
            if (cached is not null)
            {
                return cached;
            }

            if (KeyframesFor(item, clip.SourceStreamIndex, snapshot.Path) is { } index)
            {
                server.PrimeGop(snapshot.Project, clip, time, index, snapshot.Path);
            }

            return server.GetSourceFrame(snapshot.Project, clip, time, snapshot.Path, PlayDirection.Still);
        }

        return server.GetSourceFrame(snapshot.Project, clip, time, snapshot.Path, direction, mode);
    }

    /// <summary>A source's keyframe index, from the cache database or built by a scan.</summary>
    private KeyframeIndex? KeyframesFor(MediaItem item, int streamIndex, string projectPath)
    {
        var key = (item.Hash, streamIndex);
        if (_keyframes.TryGetValue(key, out KeyframeIndex? known))
        {
            return known;
        }

        KeyframeIndex? index = null;
        try
        {
            index = _cacheManager is null ? null : KeyframeIndex.Load(_cacheManager, item.Hash, streamIndex);

            if (index is null)
            {
                string path = projectPath.Length == 0 ? item.RelativePath : ProjectPaths.Resolve(projectPath, item.RelativePath);
                long started = Stopwatch.GetTimestamp();
                index = KeyframeIndex.Build(path, streamIndex);
                _log.Information(
                    "Indexed {Count} keyframes of {Media} for reverse play in {Ms:F0} ms",
                    index.Count,
                    item.Name,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                if (_cacheManager is not null)
                {
                    index.Save(_cacheManager, item.Hash, streamIndex);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Media.Interop.FfmpegException)
        {
            // Reverse play then seeks for every frame, which is slow but still right.
            _log.Warning(exception, "Could not index the keyframes of {Media}; reverse play will seek for every frame", item.Name);
        }

        _keyframes[key] = index;
        return index;
    }

    /// <summary>
    /// Spends the time before the next frame is due decoding the ones after it, so the one that
    /// is due is already in the cache when its turn comes.
    /// </summary>
    private void DecodeAhead(
        SourceFrameServer server,
        ProjectSnapshot snapshot,
        Sequence sequence,
        Rational fps,
        long frame,
        double rate,
        Flicks frameLength)
    {
        if (rate <= 0 || rate > 2.0 || _options.DecodeAhead <= 0)
        {
            return;
        }

        // Leave a couple of milliseconds of the frame interval unspent, so a slow decode here
        // does not make the next frame late.
        double interval = frameLength.Value * 1000.0 / Flicks.PerSecond / rate;
        long deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0.0, interval - 3.0) * TicksPerMillisecond);
        int step = Math.Max(1, (int)Math.Round(rate));

        for (int ahead = 1; ahead <= _options.DecodeAhead && Stopwatch.GetTimestamp() < deadline; ahead++)
        {
            if (_disposing || _wake.WaitOne(0))
            {
                // Somebody asked for something; that comes first. Put the signal back.
                _wake.Set();
                return;
            }

            // A reversed clip is primed a group at a time when it is rendered; decoding ahead of
            // it one frame at a time would be a seek per frame.
            Flicks time = Flicks.FromFrames(frame + (ahead * step), fps);
            if (TopClip(sequence, time) is not { MediaId: { } mediaId, Reverse: false } clip
                || snapshot.Project.MediaItem(mediaId) is not { } item
                || _failedMedia.ContainsKey(item.Id))
            {
                continue;
            }

            try
            {
                server.GetSourceFrame(snapshot.Project, clip, time, snapshot.Path, PlayDirection.Forward);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The render of that frame will meet the same error and report it properly.
                _log.Debug(exception, "Decoding ahead failed at {Time}", time);
                return;
            }
        }
    }

    private int MillisecondsToNextFrame(Flicks time, long frame, Rational fps, double rate)
    {
        long nextFrame = rate > 0 ? frame + 1 : frame;
        Flicks boundary = Flicks.FromFrames(nextFrame, fps);
        double flicks = rate > 0 ? (boundary - time).Value : (time - boundary).Value + 1;
        double milliseconds = flicks * 1000.0 / Flicks.PerSecond / Math.Abs(rate);

        // Wake a little early and let the next turn find the boundary has not quite arrived;
        // waking late is a frame shown late.
        return Math.Clamp((int)Math.Floor(milliseconds - 0.5), 1, 10);
    }

    private void Present()
    {
        if (_lastFrame is not { } frame)
        {
            return;
        }

        IPreviewTarget[] targets;
        lock (_targetsGate)
        {
            targets = _targets;
        }

        foreach (IPreviewTarget target in targets)
        {
            try
            {
                target.Present(frame);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _log.Error(exception, "A preview target failed to present; it keeps its last frame");
            }
        }

        Interlocked.Increment(ref _presented);
    }

    private void Report(Flicks time, long frame, TransportState state, double rate, long now)
    {
        bool changed = (state, rate) != _lastReported;

        if (!changed && frame == _lastEventFrame)
        {
            return;
        }

        // While playing, a moving playhead is reported at most every interval; a change of
        // state, or a move while stopped, is always somebody's deliberate act and goes at once.
        if (!changed && state == TransportState.Playing && now - _lastEventTicks < Ticks(_options.PlayheadEventInterval))
        {
            return;
        }

        _lastReported = (state, rate);
        _lastEventTicks = now;
        _lastEventFrame = frame;

        try
        {
            PlayheadMoved?.Invoke(this, new PlayheadMovedEventArgs(time, frame, state, rate));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.Error(exception, "A PlayheadMoved subscriber threw");
        }
    }

    private void EnsureProgram(int width, int height)
    {
        if (_program is not null && width == _programWidth && height == _programHeight)
        {
            return;
        }

        ReleaseProgram();

        _program = _device.CreateRenderTarget(width, height, Vortice.DXGI.Format.B8G8R8A8_UNorm);
        _programView = _device.Device.CreateRenderTargetView(_program);
        _programWidth = width;
        _programHeight = height;
    }

    private void ReleaseProgram()
    {
        _lastFrame = null;
        _programView?.Dispose();
        _programView = null;
        _program?.Dispose();
        _program = null;
        _programWidth = 0;
        _programHeight = 0;
    }

    /// <summary>A project and where it lives, swapped as one reference.</summary>
    private sealed record ProjectSnapshot(Project Project, string Path, long Version);

    /// <summary>What decides whether the frame on screen is still the right one.</summary>
    private readonly record struct RenderKey(long Version, long Frame, PreviewQuality Quality);
}
