using JazzHands.Core.Effects;

namespace JazzHands.Audio.Effects;

/// <summary>
/// Turns loud passages down: a feed-forward compressor with a soft knee.
/// </summary>
/// <remarks>
/// <para>
/// The level is the loudest channel's (the channels are linked, so a stereo image does not lean),
/// by its peak or by its RMS over 10 ms. Over the threshold, every <c>ratio</c> decibels in is one
/// out; within the knee the curve bends smoothly into that line. The gain reduction follows the
/// level with the attack and release times, in decibels, and makeup gain is added after.
/// </para>
/// <para>
/// No lookahead, on purpose: an effect on a clip or a track that looked ahead would put its track
/// late against the others, and every other track would have to wait for it. A 10 ms attack
/// lets a transient's first milliseconds through, which is what a compressor on a voice sounds
/// like anyway; the master's limiter (<see cref="TruePeakLimiter"/>) is the one that looks ahead.
/// </para>
/// </remarks>
[AudioEffect("audio.compressor", Name = "Compressor", Category = "Dynamics", Description = "Turns loud passages down so the level is steadier: over the threshold, every ratio decibels in comes out as one.")]
[Param("threshold", ParamType.Float, Default = "-18", Min = -60, Max = 0, Unit = "dB", Description = "The level where it starts to turn down.")]
[Param("ratio", ParamType.Float, Default = "4", Min = 1, Max = 20, SliderMax = 10, Description = "How hard it turns down above the threshold: 4 means 4 dB over comes out as 1 dB over.")]
[Param("attack", ParamType.Float, Default = "10", Min = 0.1, Max = 200, SliderMax = 100, Unit = "ms", Description = "How quickly it turns down when the level rises.")]
[Param("release", ParamType.Float, Default = "100", Min = 5, Max = 2000, SliderMax = 500, Unit = "ms", Description = "How quickly it lets go when the level falls.")]
[Param("knee", ParamType.Float, Default = "6", Min = 0, Max = 24, Unit = "dB", Description = "How gently it starts: the width around the threshold over which it eases in.")]
[Param("makeup", ParamType.Float, Default = "0", Min = 0, Max = 24, Unit = "dB", Description = "Gain after it, to bring the quieter result back up.")]
[Param("detect", ParamType.Enum, Default = "rms", Choices = "rms, peak", Animatable = false, Description = "RMS follows loudness smoothly; peak catches every transient.")]
public sealed class CompressorEffect : AudioEffect
{
    private float _envelopeDb;
    private float _meanSquare;

    /// <summary>The gain reduction at the end of the last block, in dB (0 or less), for a meter.</summary>
    public float ReductionDb => _envelopeDb;

    /// <summary>The static curve: how many dB a level is turned down by at these settings (0 or less).</summary>
    public static float Reduction(float levelDb, float threshold, float ratio, float knee)
    {
        float over = levelDb - threshold;
        float slope = (1.0f / Math.Max(ratio, 1.0f)) - 1.0f;
        if (knee > 0.0f && Math.Abs(over) <= knee / 2.0f)
        {
            float into = over + (knee / 2.0f);
            return slope * into * into / (2.0f * knee);
        }

        return over > 0.0f ? slope * over : 0.0f;
    }

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        int frames = block.Frames;
        int channels = block.Channels;
        int rate = block.SampleRate;
        bool rms = block.From[6] >= 0.5f;
        float average = Coefficient(10.0f, rate);

        for (int index = 0; index < frames; index++)
        {
            float t = (float)index / frames;
            float threshold = Lerp(block.From[0], block.To[0], t);
            float ratio = Lerp(block.From[1], block.To[1], t);
            float attack = Coefficient(Lerp(block.From[2], block.To[2], t), rate);
            float release = Coefficient(Lerp(block.From[3], block.To[3], t), rate);
            float knee = Lerp(block.From[4], block.To[4], t);
            float makeup = Lerp(block.From[5], block.To[5], t);

            float peak = 0.0f;
            for (int channel = 0; channel < channels; channel++)
            {
                peak = Math.Max(peak, Math.Abs(block.Plane(channel)[index]));
            }

            float level;
            if (rms)
            {
                _meanSquare += ((peak * peak) - _meanSquare) * average;
                level = Dsp.GainToDb(MathF.Sqrt(_meanSquare));
            }
            else
            {
                level = Dsp.GainToDb(peak);
            }

            float wanted = Reduction(level, threshold, ratio, knee);
            _envelopeDb += (wanted - _envelopeDb) * (wanted < _envelopeDb ? attack : release);
            float gain = Dsp.DbToGain(_envelopeDb + makeup);

            for (int channel = 0; channel < channels; channel++)
            {
                block.Plane(channel)[index] *= gain;
            }
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _envelopeDb = 0.0f;
        _meanSquare = 0.0f;
    }

    /// <summary>A one-pole coefficient for a time constant.</summary>
    internal static float Coefficient(float milliseconds, int sampleRate) =>
        1.0f - MathF.Exp(-1.0f / (Math.Max(milliseconds, 0.01f) * 0.001f * sampleRate));

    internal static float Lerp(float from, float to, float t) => from + ((to - from) * t);
}

/// <summary>
/// Silences what is under a threshold: background between words, a fan between lines.
/// </summary>
/// <remarks>
/// It opens when the level (the loudest channel's peak, released over 20 ms) passes the threshold
/// and closes when it falls under the threshold less the hysteresis and has stayed there for the
/// hold time, so a level hovering at the threshold does not chatter. Opening and closing are
/// ramps with the attack and release times, down to the range rather than always to silence.
/// </remarks>
[AudioEffect("audio.gate", Name = "Gate", Category = "Dynamics", Description = "Silences what is under a threshold, such as room noise between words, opening again when the sound comes back.")]
[Param("threshold", ParamType.Float, Default = "-40", Min = -80, Max = 0, Unit = "dB", Description = "The level that opens it.")]
[Param("hysteresis", ParamType.Float, Default = "4", Min = 0, Max = 20, Unit = "dB", Description = "How far under the threshold the level must fall to close it, so it does not flutter.")]
[Param("attack", ParamType.Float, Default = "1", Min = 0.1, Max = 100, SliderMax = 20, Unit = "ms", Description = "How quickly it opens.")]
[Param("hold", ParamType.Float, Default = "50", Min = 0, Max = 1000, SliderMax = 300, Unit = "ms", Description = "How long it stays open after the level drops.")]
[Param("release", ParamType.Float, Default = "100", Min = 5, Max = 2000, SliderMax = 500, Unit = "ms", Description = "How quickly it closes.")]
[Param("range", ParamType.Float, Default = "-80", Min = -80, Max = 0, Unit = "dB", Description = "How far down it goes when closed: -80 is silence, -12 only softens.")]
public sealed class GateEffect : AudioEffect
{
    private float _level;
    private float _gain = 1.0f;
    private bool _open = true;
    private long _heldSince;
    private long _sample;

    /// <summary>True when it is open.</summary>
    public bool IsOpen => _open;

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        int frames = block.Frames;
        int channels = block.Channels;
        int rate = block.SampleRate;
        float fall = CompressorEffect.Coefficient(20.0f, rate);

        for (int index = 0; index < frames; index++, _sample++)
        {
            float t = (float)index / frames;
            float threshold = Dsp.DbToGain(CompressorEffect.Lerp(block.From[0], block.To[0], t));
            float close = Dsp.DbToGain(CompressorEffect.Lerp(block.From[0] - block.From[1], block.To[0] - block.To[1], t));
            float attack = CompressorEffect.Coefficient(CompressorEffect.Lerp(block.From[2], block.To[2], t), rate);
            long hold = (long)(CompressorEffect.Lerp(block.From[3], block.To[3], t) * 0.001f * rate);
            float release = CompressorEffect.Coefficient(CompressorEffect.Lerp(block.From[4], block.To[4], t), rate);
            float floor = Dsp.DbToGain(CompressorEffect.Lerp(block.From[5], block.To[5], t));

            float peak = 0.0f;
            for (int channel = 0; channel < channels; channel++)
            {
                peak = Math.Max(peak, Math.Abs(block.Plane(channel)[index]));
            }

            _level = peak > _level ? peak : _level + ((peak - _level) * fall);

            if (_level >= threshold)
            {
                _open = true;
                _heldSince = _sample;
            }
            else if (_open && _level < close && _sample - _heldSince >= hold)
            {
                _open = false;
            }

            float target = _open ? 1.0f : floor;
            _gain += (target - _gain) * (target > _gain ? attack : release);

            for (int channel = 0; channel < channels; channel++)
            {
                block.Plane(channel)[index] *= _gain;
            }
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _level = 0.0f;
        _gain = 1.0f;
        _open = true;
        _heldSince = 0;
        _sample = 0;
    }
}

/// <summary>
/// Softens sibilance: the hiss of s and sh in a voice, turned down only when it is loud.
/// </summary>
/// <remarks>
/// A band pass around the frequency listens for sibilance (a Q of 1.5, the loudest channel's,
/// released over 60 ms). Over the threshold, the part of the voice above the frequency, less a
/// quarter, is turned down: by three quarters of how far the band is over, at most the range.
/// The rest of the voice is untouched, so a de-esser does not duck the whole word the way a
/// compressor would. The high part is split off with a 12 dB an octave high pass and turned down
/// in place, so with nothing to do it passes the voice exactly.
/// </remarks>
[AudioEffect("audio.de-esser", Name = "De-esser", Category = "Dynamics", Description = "Turns down harsh s and sh sounds in a voice without dulling the rest of it.")]
[Param("frequency", ParamType.Float, Default = "6500", Min = 2000, Max = 12000, Unit = "Hz", Description = "Where the sibilance is: 5 to 8 kHz for most voices.")]
[Param("threshold", ParamType.Float, Default = "-28", Min = -60, Max = 0, Unit = "dB", Description = "How loud the sibilance must be before it is turned down.")]
[Param("range", ParamType.Float, Default = "10", Min = 0, Max = 24, Unit = "dB", Description = "The most it turns the sibilance down.")]
public sealed class DeEsserEffect : AudioEffect
{
    private Biquad[] _listen = [];
    private Biquad[] _split = [];
    private float _builtFrequency = float.NaN;
    private float _level;
    private float _reductionDb;

    /// <summary>The reduction at the end of the last block, in dB (0 or less).</summary>
    public float ReductionDb => _reductionDb;

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        int frames = block.Frames;
        int channels = Math.Min(block.Channels, Channels);
        int rate = block.SampleRate;
        float fall = CompressorEffect.Coefficient(60.0f, rate);
        float attack = CompressorEffect.Coefficient(1.0f, rate);
        float frequency = block.To[0];

        if (frequency != _builtFrequency)
        {
            _builtFrequency = frequency;
            Biquad listen = Biquad.BandPass(rate, frequency, 1.5);
            Biquad split = Biquad.HighPass(rate, frequency * 0.75);
            for (int channel = 0; channel < _listen.Length; channel++)
            {
                _listen[channel].Take(listen);
                _split[channel].Take(split);
            }
        }

        for (int index = 0; index < frames; index++)
        {
            float t = (float)index / frames;
            float threshold = CompressorEffect.Lerp(block.From[1], block.To[1], t);
            float range = CompressorEffect.Lerp(block.From[2], block.To[2], t);

            float band = 0.0f;
            for (int channel = 0; channel < channels; channel++)
            {
                band = Math.Max(band, Math.Abs(_listen[channel].Process(block.Plane(channel)[index])));
            }

            _level = band > _level ? _level + ((band - _level) * attack) : _level + ((band - _level) * fall);
            float over = Dsp.GainToDb(_level) - threshold;
            _reductionDb = over > 0.0f ? -Math.Min(range, over * 0.75f) : 0.0f;
            float gain = Dsp.DbToGain(_reductionDb);

            for (int channel = 0; channel < channels; channel++)
            {
                Span<float> plane = block.Plane(channel);
                float high = _split[channel].Process(plane[index]);
                plane[index] += (gain - 1.0f) * high;
            }
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        for (int channel = 0; channel < _listen.Length; channel++)
        {
            _listen[channel].Reset();
            _split[channel].Reset();
        }

        _level = 0.0f;
        _reductionDb = 0.0f;
    }

    /// <inheritdoc />
    protected override void OnPrepare(int sampleRate, int channels)
    {
        _listen = new Biquad[channels];
        _split = new Biquad[channels];
        _builtFrequency = float.NaN;
    }
}

/// <summary>
/// Keeps a clip or a track under a ceiling, with no lookahead so it adds no delay.
/// </summary>
/// <remarks>
/// Instant attack on the sample peak and a smooth release (<see cref="Limiter"/>): nothing passes
/// the ceiling, and a hard transient is turned down on the sample it arrives. The master has the
/// true peak limiter that looks ahead; this is for holding one track's level where it goes.
/// </remarks>
[AudioEffect("audio.limiter", Name = "Limiter", Category = "Dynamics", Description = "Stops a clip or a track going over a ceiling; the master has its own true peak limiter at the end.")]
[Param("ceiling", ParamType.Float, Default = "-1", Min = -24, Max = 0, Unit = "dB", Description = "The level nothing passes.")]
[Param("release", ParamType.Float, Default = "50", Min = 1, Max = 1000, SliderMax = 300, Unit = "ms", Description = "How quickly it lets go after a peak.")]
public sealed class LimiterEffect : AudioEffect
{
    private Limiter? _limiter;

    /// <summary>The deepest it turned down during the last block, in dB (0 or less).</summary>
    public float ReductionDb => _limiter?.LastReductionDb ?? 0.0f;

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        if (_limiter is not { } limiter)
        {
            return;
        }

        limiter.CeilingDb = block.To[0];
        if (limiter.ReleaseMilliseconds != block.To[1])
        {
            limiter.ReleaseMilliseconds = block.To[1];
        }

        limiter.Process(block.Buffer, block.Offset, block.Frames, block.Channels);
    }

    /// <inheritdoc />
    public override void Reset() => _limiter?.Reset();

    /// <inheritdoc />
    protected override void OnPrepare(int sampleRate, int channels) => _limiter = new Limiter(sampleRate);
}
