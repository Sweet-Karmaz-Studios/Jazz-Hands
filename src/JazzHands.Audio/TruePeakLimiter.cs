namespace JazzHands.Audio;

/// <summary>
/// The master's brickwall limiter: no reconstructed peak passes the ceiling, in dBTP.
/// </summary>
/// <remarks>
/// <para>
/// It looks 5 ms ahead. Each sample's true peak (<see cref="TruePeak"/>, the sample and three
/// points to the next) gives the gain that sample may have; the gain applied is the smallest of
/// those over the next <c>A</c> samples, averaged over the last <c>A</c>. Every average of
/// minimums over windows that all contain a sample is at most that sample's gain, so the ceiling
/// holds exactly, and the gain arrives at a peak as a straight ramp over <c>A</c> samples rather
/// than a step. It recovers with a smooth release. The signal comes out 5 ms late, which the graph
/// takes up by mixing 5 ms ahead (<see cref="AudioGraph"/>), so nothing is late against the
/// picture.
/// </para>
/// <para>
/// The true peak estimate is eight times oversampled, and a peak between those points can be a
/// fraction of a decibel higher; the gain also moves, which spreads a peak a little. So the gain
/// aims 0.2 dB under the ceiling, and the square wave test measures the result eight times
/// oversampled. Off, it passes everything through with the same delay, so turning it on or off
/// never moves the sound in time.
/// </para>
/// </remarks>
public sealed class TruePeakLimiter
{
    /// <summary>How far under the ceiling the gain aims, for what the estimate cannot see.</summary>
    public const float MarginDb = 0.2f;

    private readonly int _channels;
    private readonly float[][] _lines;
    private readonly TruePeak[] _detectors;
    private readonly int _attack;
    private readonly float[] _dequeValue;
    private readonly long[] _dequeIndex;
    private readonly float[] _minimums;
    private readonly float _release;
    private int _dequeHead;
    private int _dequeCount;
    private long _count;
    private int _minimumAt;
    private double _minimumSum;
    private float _gain = 1.0f;
    private int _lineAt;
    private float _ceilingDb = -1.0f;
    private float _target;

    /// <summary>Creates a limiter.</summary>
    /// <param name="sampleRate">The mix rate.</param>
    /// <param name="channels">The mix's channels, which are limited together.</param>
    /// <param name="lookaheadMilliseconds">How far ahead it looks, which is also how late it is.</param>
    /// <param name="releaseMilliseconds">How long the gain takes to recover most of the way.</param>
    public TruePeakLimiter(int sampleRate, int channels, double lookaheadMilliseconds = 5.0, double releaseMilliseconds = 80.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        _channels = Math.Min(channels, Dsp.MaxChannels);
        Latency = Math.Max(TruePeak.Delay + 2, (int)Math.Round(lookaheadMilliseconds * sampleRate / 1000.0));
        _attack = Latency - TruePeak.Delay + 1;
        _lines = [.. Enumerable.Range(0, _channels).Select(_ => new float[Latency])];
        _detectors = [.. Enumerable.Range(0, _channels).Select(_ => new TruePeak())];
        _dequeValue = new float[_attack + 1];
        _dequeIndex = new long[_attack + 1];
        _minimums = new float[_attack];
        _release = (float)(1.0 - Math.Exp(-1.0 / (releaseMilliseconds * 0.001 * sampleRate)));
        CeilingDb = -1.0f;
        Reset();
    }

    /// <summary>How many samples late the output is.</summary>
    public int Latency { get; }

    /// <summary>False to pass everything through, still <see cref="Latency"/> late.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The ceiling in dBTP, at most 0.</summary>
    public float CeilingDb
    {
        get => _ceilingDb;
        set
        {
            _ceilingDb = Math.Min(value, 0.0f);
            _target = Dsp.DbToGain(_ceilingDb - MarginDb);
        }
    }

    /// <summary>The deepest the gain went during the last call, in dB. Zero when nothing was touched.</summary>
    public float LastReductionDb { get; private set; }

    /// <summary>Limits a range of a buffer in place; what comes out is what went in <see cref="Latency"/> samples before.</summary>
    public void Process(AudioBuffer buffer, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        int channels = Math.Min(_channels, buffer.Channels);
        float deepest = 1.0f;
        float target = _target;
        bool enabled = Enabled;

        for (int index = offset; index < offset + frames; index++)
        {
            // How loud the reconstructed signal is TruePeak.Delay samples ago, and so what gain
            // that sample may have.
            float peak = 0.0f;
            for (int channel = 0; channel < channels; channel++)
            {
                peak = Math.Max(peak, _detectors[channel].Push(buffer.Plane(channel)[index]));
            }

            float allowed = enabled && peak > target ? target / peak : 1.0f;

            // The smallest allowed gain over the last A of them, by a monotonic queue.
            while (_dequeCount > 0 && _dequeValue[Slot(_dequeCount - 1)] >= allowed)
            {
                _dequeCount--;
            }

            _dequeValue[Slot(_dequeCount)] = allowed;
            _dequeIndex[Slot(_dequeCount)] = _count;
            _dequeCount++;
            while (_dequeIndex[_dequeHead] <= _count - _attack)
            {
                _dequeHead = (_dequeHead + 1) % _dequeValue.Length;
                _dequeCount--;
            }

            _count++;

            // Averaged over the last A minimums: a straight ramp into every peak.
            float minimum = _dequeValue[_dequeHead];
            _minimumSum += minimum - _minimums[_minimumAt];
            _minimums[_minimumAt] = minimum;
            _minimumAt = (_minimumAt + 1) % _attack;
            float smoothed = (float)(_minimumSum / _attack);

            _gain = smoothed < _gain ? smoothed : _gain + ((smoothed - _gain) * _release);
            if (smoothed - _gain < 1e-6f)
            {
                // Close enough to be there: an exponential release otherwise never quite arrives.
                _gain = smoothed;
            }

            float gain = Math.Min(_gain, 1.0f);
            deepest = Math.Min(deepest, gain);

            for (int channel = 0; channel < channels; channel++)
            {
                Span<float> plane = buffer.Plane(channel);
                float incoming = plane[index];
                plane[index] = _lines[channel][_lineAt] * gain;
                _lines[channel][_lineAt] = incoming;
            }

            _lineAt = (_lineAt + 1) % Latency;
        }

        LastReductionDb = deepest >= 1.0f ? 0.0f : Dsp.GainToDb(deepest);
    }

    /// <summary>Empties the delay and forgets the gain, for a jump to somewhere else in the timeline.</summary>
    public void Reset()
    {
        foreach (float[] line in _lines)
        {
            Array.Clear(line);
        }

        foreach (TruePeak detector in _detectors)
        {
            detector.Reset();
        }

        Array.Fill(_minimums, 1.0f);
        _minimumSum = _attack;
        _minimumAt = 0;
        _dequeHead = 0;
        _dequeCount = 0;
        _count = 0;
        _lineAt = 0;
        _gain = 1.0f;
        LastReductionDb = 0.0f;
    }

    private int Slot(int fromHead) => (_dequeHead + fromHead) % _dequeValue.Length;
}
