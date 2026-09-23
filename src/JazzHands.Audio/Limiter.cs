namespace JazzHands.Audio;

/// <summary>
/// Keeps the master under a ceiling, last in the chain.
/// </summary>
/// <remarks>
/// Instant attack and a smooth release, with no lookahead. Instant attack means no sample ever
/// passes the ceiling, which is the promise a limiter makes; the price is that a transient is
/// turned down on the sample it arrives rather than eased into, which on a peak that only just
/// crosses is inaudible and on a hard one is a little crunchy.
///
/// No lookahead because lookahead is latency: five milliseconds of it would put every sample of
/// the mix five milliseconds late against the picture, and export would need to compensate as
/// well. The oversampled true peak limiter with lookahead and delay compensation belongs to the
/// DSP phase, which owns latency compensation for every effect; this is the safety net until
/// then.
/// </remarks>
public sealed class Limiter
{
    private readonly float _release;
    private float _gain = 1.0f;
    private float _ceiling;
    private float _ceilingDb;

    /// <summary>Creates a limiter.</summary>
    /// <param name="sampleRate">The mix rate, which sets how fast the release is per sample.</param>
    /// <param name="ceilingDb">The level nothing passes. -1 dBFS leaves room for a codec's overshoot.</param>
    /// <param name="releaseMilliseconds">How long the gain takes to recover most of the way.</param>
    public Limiter(int sampleRate, float ceilingDb = -1.0f, float releaseMilliseconds = 50.0f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(releaseMilliseconds);

        CeilingDb = ceilingDb;
        _release = 1.0f - MathF.Exp(-1.0f / (releaseMilliseconds * 0.001f * sampleRate));
    }

    /// <summary>False to let everything through untouched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The ceiling in dBFS.</summary>
    public float CeilingDb
    {
        get => _ceilingDb;
        set
        {
            _ceilingDb = Math.Min(value, 0.0f);
            _ceiling = Dsp.DbToGain(_ceilingDb);
        }
    }

    /// <summary>The deepest the gain went during the last block, in dB. Zero when nothing was touched.</summary>
    public float LastReductionDb { get; private set; }

    /// <summary>Limits a range of a buffer in place.</summary>
    public void Process(AudioBuffer buffer, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!Enabled)
        {
            LastReductionDb = 0.0f;
            return;
        }

        int channels = buffer.Channels;
        float ceiling = _ceiling;
        float gain = _gain;
        float deepest = 1.0f;

        for (int index = offset; index < offset + frames; index++)
        {
            float peak = 0.0f;
            for (int channel = 0; channel < channels; channel++)
            {
                peak = Math.Max(peak, Math.Abs(buffer.Plane(channel)[index]));
            }

            float wanted = peak > ceiling ? ceiling / peak : 1.0f;
            gain = wanted < gain ? wanted : gain + ((wanted - gain) * _release);
            deepest = Math.Min(deepest, gain);

            if (gain < 1.0f)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    buffer.Plane(channel)[index] *= gain;
                }
            }
        }

        _gain = gain;
        LastReductionDb = deepest >= 1.0f ? 0.0f : Dsp.GainToDb(deepest);
    }

    /// <summary>Forgets the release in progress, for a seek.</summary>
    public void Reset() => _gain = 1.0f;
}
