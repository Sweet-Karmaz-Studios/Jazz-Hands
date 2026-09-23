using JazzHands.Audio;
using JazzHands.Audio.Output;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Audio;
using JazzHands.Engine.Commands;
using Serilog;

namespace JazzHands.Engine.Playback;

/// <summary>What the transport is doing.</summary>
public enum TransportState
{
    /// <summary>Not playing, and at the place playback last started from.</summary>
    Stopped,

    /// <summary>Playing.</summary>
    Playing,

    /// <summary>Not playing, and where it was when it stopped.</summary>
    Paused,
}

/// <summary>
/// Play, pause, stop, seek and rate, over the mixer, a decode-ahead thread and an output.
/// </summary>
/// <remarks>
/// Three threads meet here and none of them waits for another.
///
/// The output's thread calls <see cref="Render"/> for every buffer. It mixes from where it is,
/// and at the start of each buffer it looks for a request (play, pause, seek, a new rate) posted
/// since the last one; if there is one it moves there and pins the clock to the frame where the
/// move becomes audible. Requests are a few numbers under a sequence number: whoever posts takes
/// a lock against other posters, and the audio thread reads without one.
///
/// The decode thread owns the decoders. It fills the block cache for the two seconds ahead of
/// wherever the audio thread is, or is about to be, and it is what arms a play: a play request
/// is not acted on until the decode thread has been over the audio it starts with, so pressing
/// play does not begin with a gap.
///
/// Everyone else posts requests and reads <see cref="Position"/>, which is the clock: the
/// position being heard, not the position being mixed a buffer ahead of it.
///
/// Rates other than 1 move the clock at that rate with the audio silent. Pitch-kept shuttle
/// audio is Phase 09's; this is the clock it will run on.
/// </remarks>
public sealed class Transport : IAudioRenderCallback, IDisposable
{
    /// <summary>How far ahead of the playhead the decode thread keeps the cache filled.</summary>
    private static readonly TimeSpan DecodeAhead = TimeSpan.FromSeconds(2);

    private readonly ILogger _log = Log.ForContext<Transport>();
    private readonly IAudioOutput _output;
    private readonly AudioBlockCache _cache;
    private readonly AudioSampleServer _server;
    private readonly AudioGraph _graph;
    private readonly Thread _decoder;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _post = new();
    private volatile bool _disposing;
    private Session? _session;

    // The latest request, under a sequence number. Written under _post, read by the audio thread
    // without it.
    private long _requestSequence;
    private bool _requestPlaying;
    private long _requestSample;
    private double _requestRate = 1.0;
    private long _armedSequence;

    // The audio thread's own state.
    private long _appliedSequence;
    private bool _playing;
    private double _rate = 1.0;
    private long _renderSample;
    private long _renderPosition;

    private float _monitorGain = 1.0f;

    // What the controlling side last asked for, for State and Stop.
    private TransportState _state = TransportState.Stopped;
    private long _playedFrom;

    // Scrub grains. The control side posts a position under a sequence number, the decode thread
    // arms it once the audio is there, and the audio thread plays it.
    private long _grainRequest;
    private long _grainSample;
    private long _grainArmed;
    private long _grainTaken;
    private long _grainPosition;
    private int _grainLeft;
    private bool _graphDirty;
    private readonly int _grainLength;
    private readonly int _grainFade;

    /// <summary>Creates a transport and starts its output, silent until something plays.</summary>
    /// <param name="output">Where the sound goes. Owned by the transport from here on.</param>
    /// <param name="cache">The block cache, which may be shared with export.</param>
    public Transport(IAudioOutput output, AudioBlockCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        _output = output;
        _cache = cache ?? new AudioBlockCache();
        _server = new AudioSampleServer(_cache, output.SampleRate, AudioReadMode.Realtime);
        _graph = new AudioGraph(_server, output.SampleRate, output.Channels);
        Clock = new PlaybackClock(output);

        // Forty milliseconds of sound around each scrub position, faded in and out over five so a
        // grain starts and stops without a click.
        _grainLength = output.SampleRate * 40 / 1000;
        _grainFade = output.SampleRate * 5 / 1000;

        _decoder = new Thread(DecodeLoop) { IsBackground = true, Name = "Jazz audio decode" };
        _decoder.Start();
        _output.Start(this);
    }

    /// <summary>
    /// A transport on the default sound card, or on a silent clock when there is none.
    /// </summary>
    public static Transport ForDefaultDevice(int sampleRate, int channels, string? deviceId = null) =>
        new(WasapiOutput.HasDevice()
            ? new WasapiOutput(sampleRate, channels, deviceId)
            : new SilentAudioOutput(sampleRate, channels));

    /// <summary>The clock: where the playhead is, as heard.</summary>
    public PlaybackClock Clock { get; }

    /// <summary>The position being heard now.</summary>
    public Flicks Position => Clock.Now;

    /// <summary>
    /// Where the playhead is as far as a caller is concerned: the clock once the last request has
    /// been heard, and the requested position until then.
    /// </summary>
    /// <remarks>
    /// The clock only moves when the audio thread applies a request, a buffer or so after it was
    /// posted. Two frame steps in quick succession, or a play straight after a seek, must start
    /// from where the previous request put the playhead rather than from a clock that has not
    /// heard about it yet.
    /// </remarks>
    public Flicks Playhead
    {
        get
        {
            lock (_post)
            {
                return Flicks.FromSamples(Here(), _output.SampleRate);
            }
        }
    }

    /// <summary>Playing, paused or stopped, as last asked.</summary>
    public TransportState State
    {
        get
        {
            lock (_post)
            {
                return _state;
            }
        }
    }

    /// <summary>The playback rate. 1 is normal speed; anything else is silent for now.</summary>
    public double Rate
    {
        get => Volatile.Read(ref _requestRate);
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A rate has to be a finite number other than zero; pause to stop.");
            }

            lock (_post)
            {
                Post(_requestPlaying, Here(), value);
            }
        }
    }

    /// <summary>
    /// How loud the speakers are, as a linear gain after the mix: the monitor knob. It changes
    /// what is heard and nothing else, so the meters, the mix and an export never see it.
    /// </summary>
    public float MonitorGain
    {
        get => Volatile.Read(ref _monitorGain);
        set => Volatile.Write(ref _monitorGain, Math.Clamp(value, 0.0f, 4.0f));
    }

    /// <summary>Plays a short grain of sound at each scrub position while stopped. On by default.</summary>
    public bool ScrubAudio { get; set; } = true;

    /// <summary>
    /// True when rates other than 1 keep their pitch. False until the stretcher arrives: shuttling
    /// is silent.
    /// </summary>
    public bool CanStretch => false;

    /// <summary>Scrub grains played, for tests and diagnostics.</summary>
    public long GrainsPlayed => Volatile.Read(ref _grainTaken);

    /// <summary>True once the audio thread is actually moving: a play has been armed and applied.</summary>
    public bool IsRolling => Volatile.Read(ref _appliedSequence) == Volatile.Read(ref _requestSequence) && Volatile.Read(ref _playing);

    /// <summary>The mixer, for meters and diagnostics.</summary>
    public AudioGraph Graph => _graph;

    /// <summary>The output.</summary>
    public IAudioOutput Output => _output;

    /// <summary>Times the device ran dry.</summary>
    public long Underruns => _output.Underruns;

    /// <summary>Blocks mixed with a source that was not decoded in time.</summary>
    public long StarvedBlocks => _graph.StarvedBlocks;

    /// <summary>The meter readings, for the UI to drain.</summary>
    public MeterRing Meters => _graph.Meter.Readings;

    /// <summary>Mixes a project from now on. Any thread.</summary>
    /// <param name="project">The project.</param>
    /// <param name="projectPath">Where it lives, for relative media paths.</param>
    public void Load(Project project, string projectPath = "")
    {
        ArgumentNullException.ThrowIfNull(project);

        MixSnapshot snapshot = AudioGraphBuilder.Build(project);
        if (snapshot.SampleRate != _output.SampleRate || snapshot.Channels != _output.Channels)
        {
            throw new InvalidOperationException(
                $"The project mixes {snapshot.Channels} channels at {snapshot.SampleRate} Hz and the output plays "
                + $"{_output.Channels} at {_output.SampleRate} Hz. A change of audio format needs a new transport.");
        }

        // Names first, so every media id in the new mix already resolves when the mix arrives.
        _server.Update(project, projectPath);
        _graph.Publish(snapshot);
        _wake.Set();
    }

    /// <summary>Follows a session: loads its project now and again after every command.</summary>
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

    /// <summary>Plays from where the playhead is.</summary>
    public void Play()
    {
        lock (_post)
        {
            if (_state == TransportState.Playing)
            {
                return;
            }

            long from = Here();
            _playedFrom = from;
            _state = TransportState.Playing;
            Post(playing: true, from, _requestRate);
        }
    }

    /// <summary>Stops where it is.</summary>
    public void Pause()
    {
        lock (_post)
        {
            if (_state != TransportState.Playing)
            {
                return;
            }

            _state = TransportState.Paused;
            Post(playing: false, Here(), _requestRate);
        }
    }

    /// <summary>Stops and goes back to where playback last started.</summary>
    public void Stop()
    {
        lock (_post)
        {
            _state = TransportState.Stopped;
            Post(playing: false, _playedFrom, _requestRate);
        }
    }

    /// <summary>Moves the playhead. Playing carries on from there; paused stays paused there.</summary>
    public void Seek(Flicks time)
    {
        long sample = Math.Max(0, time.ToSamples(_output.SampleRate, RoundingMode.Floor));

        lock (_post)
        {
            if (_state != TransportState.Playing)
            {
                _playedFrom = sample;
            }

            Post(_state == TransportState.Playing, sample, _requestRate);
        }
    }

    /// <summary>
    /// Plays a grain of sound at a position while stopped: what makes dragging the playhead
    /// audible. Ignored while playing, and when <see cref="ScrubAudio"/> is off.
    /// </summary>
    /// <remarks>
    /// The grain is centred on the position and plays once the decode thread has the audio for
    /// it, usually within a buffer. A newer grain replaces one still playing, so a fast drag
    /// sounds like a run of short grains rather than a queue of stale ones.
    /// </remarks>
    public void Scrub(Flicks time)
    {
        if (!ScrubAudio)
        {
            return;
        }

        long sample = Math.Max(0, time.ToSamples(_output.SampleRate, RoundingMode.Floor));

        lock (_post)
        {
            if (_state == TransportState.Playing)
            {
                return;
            }

            Volatile.Write(ref _grainSample, sample);
            Interlocked.Increment(ref _grainRequest);
        }

        _wake.Set();
    }

    /// <summary>Waits until a play has actually started, for tests and scripts. Not for the UI thread.</summary>
    public bool WaitUntilRolling(TimeSpan timeout) =>
        SpinWait.SpinUntil(() => IsRolling, timeout);

    /// <inheritdoc />
    void IAudioRenderCallback.Render(AudioBuffer output, int frames, long submitted) => Render(output, frames, submitted);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposing)
        {
            return;
        }

        Detach();
        _disposing = true;
        _output.Stop();
        _wake.Set();
        _decoder.Join();
        _output.Dispose();
        _wake.Dispose();
    }

    /// <summary>
    /// The audio thread's whole job: apply a request if one is waiting, then mix.
    /// </summary>
    /// <remarks>No lock, no await, no log, no allocation.</remarks>
    internal void Render(AudioBuffer output, int frames, long submitted)
    {
        long sequence = Volatile.Read(ref _requestSequence);

        if (sequence != _appliedSequence && (sequence & 1) == 0)
        {
            bool playing = Volatile.Read(ref _requestPlaying);
            long sample = Volatile.Read(ref _requestSample);
            double rate = Volatile.Read(ref _requestRate);

            bool consistent = sequence == Volatile.Read(ref _requestSequence);
            bool armed = !playing || Volatile.Read(ref _armedSequence) >= sequence;

            if (consistent && armed)
            {
                // A grain moves the graph somewhere else, so the next play starts it afresh even
                // when it resumes exactly where it paused.
                if (sample != _renderSample || _graphDirty)
                {
                    _graph.Reset();
                    _graphDirty = false;
                }

                if (playing)
                {
                    _grainLeft = 0;
                }

                _playing = playing;
                _rate = rate;
                _renderSample = sample;
                Clock.Anchor(sample, submitted, rate, playing);
                Volatile.Write(ref _appliedSequence, sequence);
            }
        }

        if (!_playing)
        {
            output.Clear(0, frames);
            RenderGrain(output, frames);
            return;
        }

        if (_rate == 1.0)
        {
            _graph.Pull(_renderSample, output, 0, frames);
            _renderSample += frames;

            float monitor = Volatile.Read(ref _monitorGain);
            if (monitor != 1.0f)
            {
                for (int channel = 0; channel < output.Channels; channel++)
                {
                    Span<float> plane = output.Plane(channel, 0, frames);
                    for (int index = 0; index < frames; index++)
                    {
                        plane[index] *= monitor;
                    }
                }
            }
        }
        else
        {
            output.Clear(0, frames);
            _renderSample = Math.Max(0, _renderSample + (long)Math.Round(frames * _rate));
        }

        Volatile.Write(ref _renderPosition, _renderSample);
    }

    /// <summary>
    /// Plays the newest armed scrub grain into the start of a silent buffer. Audio thread only;
    /// no lock, no allocation.
    /// </summary>
    private void RenderGrain(AudioBuffer output, int frames)
    {
        long armed = Volatile.Read(ref _grainArmed);
        if (armed > _grainTaken)
        {
            _grainPosition = Math.Max(0, Volatile.Read(ref _grainSample) - (_grainLength / 2));
            _grainLeft = _grainLength;
            _graph.Reset();
            _graphDirty = true;
            Volatile.Write(ref _grainTaken, armed);
        }

        if (_grainLeft <= 0)
        {
            return;
        }

        int count = Math.Min(frames, _grainLeft);
        int played = _grainLength - _grainLeft;
        _graph.Pull(_grainPosition, output, 0, count);

        float monitor = Volatile.Read(ref _monitorGain);
        for (int channel = 0; channel < output.Channels; channel++)
        {
            Span<float> plane = output.Plane(channel, 0, count);
            for (int index = 0; index < count; index++)
            {
                plane[index] *= GrainEnvelope(played + index) * monitor;
            }
        }

        _grainPosition += count;
        _grainLeft -= count;
    }

    /// <summary>A raised cosine at each end of the grain, flat in between.</summary>
    private float GrainEnvelope(int index)
    {
        int fromEnd = _grainLength - 1 - index;
        int edge = Math.Min(index, fromEnd);
        if (edge >= _grainFade)
        {
            return 1.0f;
        }

        float phase = (float)edge / _grainFade;
        return 0.5f - (0.5f * MathF.Cos(MathF.PI * phase));
    }

    /// <summary>
    /// The sample the playhead is at: the clock when the audio thread has applied everything
    /// posted, and the last request otherwise. Call under <see cref="_post"/>.
    /// </summary>
    private long Here() =>
        Volatile.Read(ref _appliedSequence) == _requestSequence
            ? Clock.Sample
            : Volatile.Read(ref _requestSample);

    /// <summary>Posts a request for the audio thread. Call under <see cref="_post"/>.</summary>
    private void Post(bool playing, long sample, double rate)
    {
        Interlocked.Increment(ref _requestSequence);
        Volatile.Write(ref _requestPlaying, playing);
        Volatile.Write(ref _requestSample, sample);
        Volatile.Write(ref _requestRate, rate);
        Interlocked.Increment(ref _requestSequence);
        _wake.Set();
    }

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            Load(e.Project, session.ProjectPath);
        }
        catch (InvalidOperationException exception)
        {
            // The project's sample rate or channel count changed under a running output. Keep
            // playing the last mix that fitted rather than failing the command that changed it;
            // the host makes a new transport for the new format.
            _log.Warning(exception, "The project's audio format changed; playback keeps the previous mix until the transport is recreated");
        }
    }

    /// <summary>
    /// Keeps the cache filled ahead of the playhead, arms plays, and reports what went wrong.
    /// </summary>
    /// <remarks>
    /// The decoders belong to this thread, so the server is disposed here too.
    /// </remarks>
    private void DecodeLoop()
    {
        var demands = new List<SourceDemand>();
        long ahead = (long)(DecodeAhead.TotalSeconds * _output.SampleRate);
        long reportedUnderruns = 0;
        long reportedStarved = 0;

        try
        {
            while (!_disposing)
            {
                _wake.WaitOne(20);

                long sequence;
                long from;

                lock (_post)
                {
                    sequence = _requestSequence;
                    from = Volatile.Read(ref _appliedSequence) == sequence
                        ? Volatile.Read(ref _renderPosition)
                        : _requestSample;
                }

                demands.Clear();
                _graph.Snapshot.CollectDemands(from, ahead, demands);

                foreach (SourceDemand demand in demands)
                {
                    _server.Prefetch(demand.Source, demand.StartSample, demand.Frames);
                }

                // A scrub grain is armed once the forty milliseconds around it are decoded.
                long grain = Volatile.Read(ref _grainRequest);
                if (grain > Volatile.Read(ref _grainArmed))
                {
                    long start = Math.Max(0, Volatile.Read(ref _grainSample) - (_grainLength / 2));

                    demands.Clear();
                    _graph.Snapshot.CollectDemands(start, _grainLength, demands);

                    foreach (SourceDemand demand in demands)
                    {
                        _server.Prefetch(demand.Source, demand.StartSample, demand.Frames);
                    }

                    Volatile.Write(ref _grainArmed, grain);
                }

                // Everything the new position starts with is decoded, so a play can begin.
                if (Volatile.Read(ref _armedSequence) < sequence)
                {
                    Volatile.Write(ref _armedSequence, sequence);
                }

                reportedUnderruns = Report(_output.Underruns, reportedUnderruns, "The audio device ran dry {Count} time(s); raise the buffer or look for a stall");
                reportedStarved = Report(_graph.StarvedBlocks, reportedStarved, "{Count} audio block(s) played before their source was decoded");
            }
        }
        catch (Exception exception)
        {
            _log.Error(exception, "The audio decode thread stopped");
        }
        finally
        {
            _server.Dispose();
        }
    }

    private long Report(long now, long reported, string message)
    {
        if (now > reported)
        {
            _log.Warning(message, now - reported);
        }

        return now;
    }
}
