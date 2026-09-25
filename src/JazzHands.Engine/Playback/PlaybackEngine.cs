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
using JazzHands.Render.Compositing;
using JazzHands.Render.Scopes;
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
/// context are thread affine and a second thread would mean a second copy of each. After each
/// present, the time left before the next frame is due is spent decoding ahead on every layer,
/// one frame at a time with each decode finished on the GPU before the next is issued, so the
/// frame that is due is already there and its present never queues behind a burst of decodes.
/// Parked, the same fills the frames after the playhead, and play waits for it.
///
/// Each frame goes through <see cref="FrameServer"/>: the render graph is built from the
/// sequence, every visible layer's picture fetched on its own decoder lane, and the stack
/// composited and encoded into the program texture at the working resolution.
/// </remarks>
public sealed partial class PlaybackEngine : IPlaybackController, IDisposable
{
    private static readonly double TicksPerMillisecond = Stopwatch.Frequency / 1000.0;

    private readonly ILogger _log = Log.ForContext<PlaybackEngine>();
    private readonly Transport _transport;
    private readonly RenderDevice _device;
    private readonly PlaybackOptions _options;
    private readonly DiagnosticsLog? _notices;
    private readonly CacheManager? _cacheManager;
    private readonly Caching.ProxyService? _proxies;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _targetsGate = new();
    private readonly Lock _controlGate = new();
    private readonly Func<bool> _interrupted;
    private readonly RenderOptions[] _playingOptions = new RenderOptions[5];
    private readonly RenderOptions[] _parkedOptions = new RenderOptions[5];
    private long _lastVersion = -1;

    private IPreviewTarget[] _targets = [];
    private volatile bool _disposing;
    private volatile bool _suspended;
    private bool _released;
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
    private RenderKey _prerolled;
    private long _prerolledGeneration = -1;
    private volatile bool _playWaiting;
    private readonly ManualResetEventSlim _prerollSignal = new(false);
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
    private ScopeRenderer? _scopes;
    private volatile bool _scopesWanted;
    private volatile bool _scopesStale;
    private long _scopesAt;

    private long _presented;
    private long _dropped;
    private long _rendered;
    private long _frameOnScreen;
    private int _effectiveQuality = (int)PreviewQuality.Full;

    // The render pools' counters as of the last frame, written by the composition thread and
    // read by anyone; a reader may see one frame's counts mixed with the next's, which is fine
    // for diagnostics. Order: targets created, rented, outstanding, frame textures created, layers
    // cached, layers drawn.
    private readonly long[] _renderStats = new long[6];

    /// <summary>Creates the engine and starts its composition thread. Plays nothing until asked.</summary>
    /// <param name="transport">The audio side and the master clock. Not owned.</param>
    /// <param name="device">The device frames are decoded, rendered and presented on. Not owned.</param>
    /// <param name="options">How to set up; defaults suit the editor.</param>
    /// <param name="notices">Where decoder fallbacks and missing media are reported.</param>
    /// <param name="cacheManager">Where keyframe indexes are kept between runs, for reverse play.</param>
    /// <param name="proxies">Proxies to play in place of their sources when switched on, or null for never.</param>
    public PlaybackEngine(
        Transport transport,
        RenderDevice device,
        PlaybackOptions? options = null,
        DiagnosticsLog? notices = null,
        CacheManager? cacheManager = null,
        Caching.ProxyService? proxies = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(device);

        _transport = transport;
        _device = device;
        _options = options ?? new PlaybackOptions();
        _notices = notices;
        _cacheManager = cacheManager;
        _proxies = proxies;

        // A proxy that arrives or goes, or proxies switched on or off, changes what the picture is
        // made from: a new snapshot renders the frame again and gives files that failed another
        // chance.
        proxies?.Changed += OnProxiesChanged;
        _interrupted = Interrupted;

        // Made once, so rendering a frame builds no options.
        foreach (int divisor in new[] { 1, 2, 4 })
        {
            _playingOptions[divisor] = RenderOptions.ForDivisor(divisor);
            _parkedOptions[divisor] = RenderOptions.ForDivisor(divisor) with { CacheLayers = true };
        }

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

    /// <summary>
    /// Raised on the composition thread with the scopes of the frame on screen, while
    /// <see cref="Scopes"/> is on: at most every 33 ms while playing, the latest frame's once it
    /// is parked.
    /// </summary>
    public event EventHandler<ScopeReading>? ScopesMeasured;

    /// <summary>
    /// Measure the scopes on each frame shown. Off costs nothing; the Scopes panel turns it on
    /// while it is visible and off when hidden.
    /// </summary>
    public bool Scopes
    {
        get => _scopesWanted;
        set
        {
            _scopesWanted = value;
            if (value)
            {
                _scopesStale = true;
                _wake.Set();
            }
        }
    }

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

    /// <summary>
    /// True once the frames after the parked playhead are decoded, so pressing play starts from
    /// the cache. Scripts and benchmarks wait for it the way a person looks at the first frame.
    /// </summary>
    public bool IsPrerolled => Interlocked.Read(ref _prerolledGeneration) == Interlocked.Read(ref _seekGeneration);

    /// <summary>What the render pools have done: flat creation counts while playing mean no allocation.</summary>
    public RenderStatsInfo RenderStats => new(
        Volatile.Read(ref _renderStats[0]),
        Volatile.Read(ref _renderStats[1]),
        (int)Volatile.Read(ref _renderStats[2]),
        Volatile.Read(ref _renderStats[3]),
        (int)Volatile.Read(ref _renderStats[4]),
        Volatile.Read(ref _renderStats[5]));

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
        bool starting;

        lock (_controlGate)
        {
            Sequence? sequence = _snapshot.Project.ActiveSequence;
            starting = _transport.State != TransportState.Playing;

            if (sequence is not null && starting)
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
        }

        if (starting)
        {
            WaitForPreroll();
        }

        lock (_controlGate)
        {
            if (_transport.Rate != 1.0)
            {
                _transport.Rate = 1.0;
            }

            _transport.Play();
        }

        _wake.Set();
    }

    /// <summary>
    /// Waits, briefly, until the frames after the playhead are decoded on the playhead decoder.
    /// </summary>
    /// <remarks>
    /// The audio side already waits for its first second to be decoded before a play starts; this
    /// is the same courtesy for the picture. Normally the composition thread did it while the
    /// playhead sat still and this returns at once. After a seek straight into play it costs the
    /// time to open a decoder and decode a handful of frames, which is better spent before the
    /// clock starts than as a stutter after. Never more than 300 ms.
    /// </remarks>
    private void WaitForPreroll()
    {
        long generation = Interlocked.Read(ref _seekGeneration);
        if (Interlocked.Read(ref _prerolledGeneration) == generation)
        {
            return;
        }

        _playWaiting = true;
        _wake.Set();

        long deadline = Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromMilliseconds(300));
        while (Interlocked.Read(ref _prerolledGeneration) != generation)
        {
            long left = deadline - Stopwatch.GetTimestamp();
            if (left <= 0)
            {
                _log.Debug("Starting playback before the preroll finished");
                break;
            }

            _prerollSignal.Wait(TimeSpan.FromMilliseconds(left / TicksPerMillisecond));
            _prerollSignal.Reset();
        }

        _playWaiting = false;
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
            _transport.CanStretch,
            RenderStats,
            _proxies?.Enabled ?? false);
    }

    /// <summary>Waits until the engine has presented a frame after this call, for tests and scripts.</summary>
    public bool WaitForPresent(TimeSpan timeout)
    {
        long before = PresentedFrames;
        Refresh();
        return SpinWait.SpinUntil(() => PresentedFrames > before, timeout);
    }

    /// <summary>
    /// While true the preview holds nothing of its own: playback pauses, and the composition
    /// thread lets go of its decoders, frame cache, hardware context and textures and sleeps.
    /// Set false again, it rebuilds them and draws the frame the playhead is on. For a hidden
    /// window, which must not hold video memory or wake the processor.
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (value == _suspended)
            {
                return;
            }

            if (value)
            {
                Pause();
            }

            _suspended = value;
            _wake.Set();
        }
    }

    /// <summary>True once the composition thread has let go of everything, while suspended.</summary>
    public bool Released => Volatile.Read(ref _released);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposing)
        {
            return;
        }

        Detach();
        _proxies?.Changed -= OnProxiesChanged;
        _disposing = true;
        _wake.Set();
        _thread.Join();
        _wake.Dispose();
        _prerollSignal.Dispose();
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

    private static long Ticks(TimeSpan span) => (long)(span.TotalMilliseconds * TicksPerMillisecond);

    private void OnProxiesChanged(object? sender, string? hash)
    {
        ProjectSnapshot current = _snapshot;
        Load(current.Project, current.Path);
    }

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
        FrameServer? frames = null;
        TimerResolution? resolution = null;

        try
        {
            _device.EnableMultithreadProtection();

            // The multimedia class scheduler gives a registered thread priority over ordinary
            // work when the machine is busy, which is when a presentation loop most needs it.
            // The audio thread does the same as "Pro Audio".
            uint taskIndex = 0;
            IntPtr mmcss = AvSetMmThreadCharacteristics("Playback", ref taskIndex);
            if (mmcss == IntPtr.Zero)
            {
                _log.Debug("The composition thread could not join the multimedia class scheduler");
            }

            while (!_disposing)
            {
                // Suspended (the window hidden): nothing is kept that only the preview needs, and
                // the thread sleeps until woken rather than ticking.
                if (_suspended)
                {
                    if (frames is not null)
                    {
                        resolution?.Dispose();
                        resolution = null;
                        ReleaseProgram();
                        _scopes?.Dispose();
                        _scopes = null;
                        frames.Dispose();
                        frames = null;
                        hardware?.Dispose();
                        hardware = null;
                        _device.ImmediateContext.Flush();
                        Volatile.Write(ref _released, true);
                        _log.Information("The preview let go of its decoders, frame cache and textures while hidden");
                    }

                    _wake.WaitOne();
                    continue;
                }

                if (frames is null)
                {
                    if (_options.HardwareDecode && _device.SupportsVideo)
                    {
                        hardware = HardwareDeviceContext.CreateShared(_device.Device.NativePointer, _device.ImmediateContext.NativePointer);
                    }

                    frames = new FrameServer(_device, hardware, _options.FrameCacheBytes, _notices, _cacheManager)
                    {
                        Substitute = _proxies is { } proxies ? proxies.Substitute : null,
                    };

                    // The frame on screen went with the textures; the same one is drawn again.
                    _lastKey = default;
                    Volatile.Write(ref _released, false);
                }

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

                int wait = Tick(frames);
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
            _scopes?.Dispose();
            _scopes = null;
            frames?.Dispose();
            hardware?.Dispose();
        }
    }

    /// <summary>
    /// One turn of the loop: settle the range, render the frame the clock is in if it is not the
    /// one already shown, decode ahead, and say how long to sleep.
    /// </summary>
    /// <returns>Milliseconds until something is next due.</returns>
    private int Tick(FrameServer frames)
    {
        ProjectSnapshot snapshot = _snapshot;
        Project project = snapshot.Project;
        Sequence? sequence = project.ActiveSequence;
        long now = Stopwatch.GetTimestamp();

        if (sequence is null)
        {
            return 50;
        }

        if (snapshot.Version != _lastVersion)
        {
            // An edit may have relinked or replaced a file that failed, so everything gets
            // another chance.
            frames.Retry();
            _lastVersion = snapshot.Version;
        }

        ProjectSettings settings = project.SettingsFor(sequence);
        Rational fps = settings.FrameRate;
        Flicks frameLength = settings.FrameDuration;

        // The generation is read before the playhead, so a seek that lands between the two makes
        // this turn look older than it is rather than newer: a preroll is then repeated, never
        // skipped.
        long generation = Interlocked.Read(ref _seekGeneration);
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

        var key = new RenderKey(snapshot.Version, frame, effective);
        bool refresh = _refresh;
        _refresh = false;

        if (key != _lastKey || _lastFrame is null)
        {
            CountDrops(frame, playing, rate, generation);
            Render(frames, snapshot, sequence, settings, frame, time, effective, playing, rate);
            _lastKey = key;
            Present();
            MeasureScopes(playing, force: false);
        }
        else if (refresh)
        {
            Present();
        }
        else if (_scopesStale)
        {
            // The scopes were just turned on over a frame already shown.
            MeasureScopes(playing, force: true);
        }

        Report(time, frame, state, playing ? rate : _transport.Rate, now);

        if (playing)
        {
            DecodeAhead(frames, snapshot, sequence, frame, rate, frameLength);
            return MillisecondsToNextFrame(time, frame, fps, rate);
        }

        // Stopped on a frame: have the next few ready on the playhead decoder, so that pressing
        // play starts from the cache rather than from opening a decoder. Once per position, and
        // abandoned the moment anything else is asked for, so it never slows a scrub.
        bool settled = Volatile.Read(ref _scrubUntilTicks) < now || _playWaiting;
        if ((_prerolled != key || Interlocked.Read(ref _prerolledGeneration) != generation) && settled)
        {
            if (!DecodeAhead(frames, snapshot, sequence, frame, 1.0, frameLength))
            {
                // Out of time with frames still to decode, which several layers each starting
                // part way through a group of pictures easily are: carry on straight away.
                return 1;
            }

            _prerolled = key;
            Interlocked.Exchange(ref _prerolledGeneration, generation);
            _prerollSignal.Set();
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
            _log.Debug("Dropped {Missed} frame(s) before frame {Frame}", missed, frame);
        }

        _lastPlayingFrame = frame;
        _lastPlayingGeneration = generation;
    }

    /// <summary>
    /// Renders the frame through the compositor into the program texture at the working
    /// resolution, and records what is now on screen.
    /// </summary>
    private void Render(
        FrameServer frames,
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

        (int width, int height) = RenderGraphBuilder.OutputSize(settings, 1.0f / divisor);
        EnsureProgram(width, height);

        Flicks time = Flicks.FromFrames(frame, settings.FrameRate);

        // Placed layers are kept between frames only while the playhead is parked: scrubbing back
        // over a still region then redraws nothing, and playing never fills the cache with
        // frames that will not come round again.
        RenderOptions options = playing ? _playingOptions[divisor] : _parkedOptions[divisor];

        frames.Motion = new Motion(playing, rate);
        frames.Render(snapshot.Project, sequence, time, options, _programView!, width, height, OutputSettings.Preview, snapshot.Path);

        RecordStats(frames);
        Interlocked.Increment(ref _rendered);
        Interlocked.Exchange(ref _frameOnScreen, frame);
        _lastFrame = new PreviewFrame(_program!, width, height, settings.Width, settings.Height, frame, time, playhead, quality);
    }

    /// <summary>Copies the render pools' counters where other threads can read them.</summary>
    private void RecordStats(FrameServer frames)
    {
        Compositor compositor = frames.Compositor;
        Volatile.Write(ref _renderStats[0], compositor.Pool.Created);
        Volatile.Write(ref _renderStats[1], compositor.Pool.Rented);
        Volatile.Write(ref _renderStats[2], compositor.Pool.Outstanding);
        Volatile.Write(ref _renderStats[3], frames.Sources.Cache.Textures.Created);
        Volatile.Write(ref _renderStats[4], compositor.Cache.Count);
        Volatile.Write(ref _renderStats[5], compositor.LayersDrawn);
    }

    /// <summary>
    /// Spends the time before the next frame is due decoding the ones after it, on every visible
    /// layer, so the frames that are due are already in the cache when their turn comes.
    /// </summary>
    /// <returns>False when it gave way to a request before finishing.</returns>
    private bool DecodeAhead(FrameServer frames, ProjectSnapshot snapshot, Sequence sequence, long frame, double rate, Flicks frameLength)
    {
        if (rate <= 0 || rate > 2.0 || _options.DecodeAhead <= 0)
        {
            return true;
        }

        // Stop a little before the next frame is due by the clock, not a frame interval from now:
        // this runs after the render and present, and counting from here would spend the next
        // frame's time too. When already late, a couple of milliseconds still go on decoding, or
        // falling behind would stop the one thing that catches up.
        double interval = frameLength.Value * 1000.0 / Flicks.PerSecond / rate;
        Flicks boundary = Flicks.FromFrames(frame + 1, snapshot.Project.SettingsFor(sequence).FrameRate);
        double untilDue = (boundary - _transport.Playhead).Value * 1000.0 / Flicks.PerSecond / rate;
        double budget = Math.Clamp(untilDue - 1.5, 2.0, interval);
        long deadline = Stopwatch.GetTimestamp() + (long)(budget * TicksPerMillisecond);
        int step = Math.Max(1, (int)Math.Round(rate));

        return frames.DecodeAhead(snapshot.Project, sequence, frame, step, _options.DecodeAhead, deadline, _interrupted, snapshot.Path);
    }

    /// <summary>True when somebody asked for something while decoding ahead; the signal is put back.</summary>
    private bool Interrupted()
    {
        if (_disposing || _wake.WaitOne(0))
        {
            _wake.Set();
            return true;
        }

        return false;
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

    /// <summary>
    /// The scopes of the frame just shown: playing, at most every 33 ms and collected a frame late
    /// so nothing waits on the GPU; parked, every pending reading drained and the latest reported,
    /// so a still frame shows its own scopes.
    /// </summary>
    private void MeasureScopes(bool playing, bool force)
    {
        _scopesStale = false;
        if (!_scopesWanted || _program is null)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (playing && !force && Stopwatch.GetElapsedTime(_scopesAt, now) < TimeSpan.FromMilliseconds(33))
        {
            return;
        }

        _scopesAt = now;
        _scopes ??= new ScopeRenderer(_device);
        _scopes.Submit(_program);

        ScopeReading? latest = playing ? _scopes.Collect() : null;
        while (!playing && _scopes.Collect(wait: true) is { } reading)
        {
            latest = reading;
        }

        if (latest is not null)
        {
            ScopesMeasured?.Invoke(this, latest);
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

    [System.Runtime.InteropServices.LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
}
