using System.Diagnostics;

namespace JazzHands.Audio.Output;

/// <summary>
/// An output that plays to nowhere in real time, for a machine with no sound card.
/// </summary>
/// <remarks>
/// A headless build server, or a remote session with audio off, still needs a transport whose
/// clock moves at the speed of time. This pulls from its callback on a timer and throws the
/// samples away, so everything downstream of the clock behaves exactly as it would with a device.
/// </remarks>
public sealed class SilentAudioOutput : IAudioOutput
{
    private readonly AudioBuffer _buffer;
    private IAudioRenderCallback? _callback;
    private Thread? _thread;
    private volatile bool _stopping;
    private long _played;

    /// <summary>Creates an output.</summary>
    public SilentAudioOutput(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        SampleRate = sampleRate;
        Channels = channels;
        _buffer = new AudioBuffer(channels, sampleRate / 10);
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public int Channels { get; }

    /// <inheritdoc />
    public bool IsRunning => _thread is not null;

    /// <inheritdoc />
    public long FramesPlayed => Interlocked.Read(ref _played);

    /// <inheritdoc />
    public long Underruns => 0;

    /// <inheritdoc />
    public string DeviceName => "silent";

    /// <inheritdoc />
    public void Start(IAudioRenderCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        _callback = callback;
        _stopping = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "Jazz silent output" };
        _thread.Start();
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopping = true;
        _thread.Join();
        _thread = null;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private void Run()
    {
        long started = Stopwatch.GetTimestamp();
        long played = 0;

        while (!_stopping)
        {
            Thread.Sleep(5);

            long due = (Stopwatch.GetTimestamp() - started) * SampleRate / Stopwatch.Frequency;
            while (played < due && !_stopping)
            {
                int count = (int)Math.Min(due - played, _buffer.Capacity);
                _callback!.Render(_buffer, count, played);
                played += count;
                Interlocked.Exchange(ref _played, played);
            }
        }
    }
}
