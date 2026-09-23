namespace JazzHands.Audio.Output;

/// <summary>
/// An output with no device behind it, advanced by whoever holds it.
/// </summary>
/// <remarks>
/// The transport and the clock are the same code whether a sound card is there or not, so they
/// can be tested on a build server that has none. <see cref="Pull"/> asks the callback for
/// samples the way a device would, and <see cref="Play"/> says the device has played some of
/// them, which is what moves the clock. Keeping the two apart is what lets a test put samples in
/// the buffer that have not been heard yet, the way a real device's latency does.
/// </remarks>
public sealed class ManualAudioOutput : IAudioOutput
{
    private readonly AudioBuffer _buffer;
    private IAudioRenderCallback? _callback;
    private long _submitted;
    private long _played;

    /// <summary>Creates an output.</summary>
    /// <param name="sampleRate">The mix rate.</param>
    /// <param name="channels">The mix channel count.</param>
    /// <param name="capacity">The most frames one <see cref="Pull"/> may ask for.</param>
    public ManualAudioOutput(int sampleRate = 48000, int channels = 2, int capacity = 4800)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        SampleRate = sampleRate;
        Channels = channels;
        _buffer = new AudioBuffer(channels, capacity);
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public int Channels { get; }

    /// <inheritdoc />
    public bool IsRunning => _callback is not null;

    /// <inheritdoc />
    public long FramesPlayed => Interlocked.Read(ref _played);

    /// <inheritdoc />
    public long Underruns => 0;

    /// <inheritdoc />
    public string DeviceName => "manual";

    /// <summary>What the last <see cref="Pull"/> produced.</summary>
    public AudioBuffer Last => _buffer;

    /// <summary>Frames handed out so far.</summary>
    public long Submitted => _submitted;

    /// <inheritdoc />
    public void Start(IAudioRenderCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
    }

    /// <inheritdoc />
    public void Stop() => _callback = null;

    /// <summary>Asks the callback for samples, as a device's buffer would. Nothing when stopped.</summary>
    /// <returns>The buffer the samples are in, valid until the next pull.</returns>
    public AudioBuffer Pull(int frames)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frames, _buffer.Capacity);

        _buffer.Clear(0, frames);

        if (_callback is { } callback)
        {
            callback.Render(_buffer, frames, _submitted);
            _submitted += frames;
        }

        return _buffer;
    }

    /// <summary>Says the device has played some frames, which moves the clock.</summary>
    public void Play(long frames)
    {
        long played = Math.Min(_played + frames, _submitted);
        Interlocked.Exchange(ref _played, played);
    }

    /// <summary>Pulls and plays in one step, for a test that has no latency to model.</summary>
    public AudioBuffer PullAndPlay(int frames)
    {
        AudioBuffer buffer = Pull(frames);
        Play(frames);
        return buffer;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
