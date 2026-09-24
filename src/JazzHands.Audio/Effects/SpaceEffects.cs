using JazzHands.Core.Effects;

namespace JazzHands.Audio.Effects;

/// <summary>
/// A room around the sound: Freeverb's eight comb filters and four all-passes a side, with a
/// pre-delay.
/// </summary>
/// <remarks>
/// <para>
/// The channels are summed into the reverb and its two sides come back across the stereo field
/// as wide as the width says; a mono source takes the average of the two. Channels past the
/// second (the centre and surrounds of 5.1) are left dry. The delay lengths are Freeverb's,
/// tuned at 44.1 kHz and scaled to the rate, and the right side's are 23 samples longer, which
/// is what makes it stereo.
/// </para>
/// <para>
/// Its tail is up to about eight seconds at the largest size, so a track with a reverb goes on
/// running its effects that long after its last clip (<see cref="TailSeconds"/>); a reverb on a
/// clip stops with the clip.
/// </para>
/// </remarks>
[AudioEffect("audio.reverb", Name = "Reverb", Category = "Space", Description = "Puts the sound in a room, from a small booth to a hall.")]
[Param("size", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How big the room is, and so how long it rings.")]
[Param("damping", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How soon the high end dies away: soft walls at 1, hard ones at 0.")]
[Param("pre-delay", ParamType.Float, Default = "20", Min = 0, Max = 200, SliderMax = 100, Unit = "ms", Label = "Pre-delay", Description = "The gap before the room answers, which separates the voice from its reverb.")]
[Param("width", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "How wide the reverb spreads: 0 is mono.")]
[Param("mix", ParamType.Float, Default = "0.25", Min = 0, Max = 1, Description = "How much reverb against the dry sound: 0 is dry, 1 all reverb.")]
public sealed class ReverbEffect : AudioEffect
{
    private static readonly int[] CombTuning = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTuning = [556, 441, 341, 225];
    private const int Spread = 23;
    private const float InputGain = 0.015f;
    private const float WetScale = 3.0f;

    private float[][] _combs = [];
    private int[] _combAt = [];
    private float[] _combStore = [];
    private float[][] _allpasses = [];
    private int[] _allpassAt = [];
    private float[] _preDelay = [];
    private int _preDelayAt;

    /// <inheritdoc />
    public override double TailSeconds => 8.0;

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        if (_combs.Length == 0)
        {
            return;
        }

        int frames = block.Frames;
        int channels = Math.Min(block.Channels, Channels);
        int preDelayLength = _preDelay.Length;
        Span<float> left = block.Plane(0);
        Span<float> right = channels > 1 ? block.Plane(1) : default;

        for (int index = 0; index < frames; index++)
        {
            float t = (float)index / frames;
            float size = CompressorEffect.Lerp(block.From[0], block.To[0], t);
            float damping = CompressorEffect.Lerp(block.From[1], block.To[1], t);
            float delayed = CompressorEffect.Lerp(block.From[2], block.To[2], t) * 0.001f * block.SampleRate;
            float width = CompressorEffect.Lerp(block.From[3], block.To[3], t);
            float mix = CompressorEffect.Lerp(block.From[4], block.To[4], t);

            float feedback = (size * 0.28f) + 0.7f;
            float damp1 = damping * 0.4f;
            float damp2 = 1.0f - damp1;

            float dryLeft = left[index];
            float dryRight = channels > 1 ? right[index] : dryLeft;
            float input = (dryLeft + dryRight) * InputGain;

            // The pre-delay: read behind, write here.
            _preDelay[_preDelayAt] = input;
            int back = Math.Clamp((int)delayed, 0, preDelayLength - 1);
            float into = _preDelay[((_preDelayAt - back) % preDelayLength + preDelayLength) % preDelayLength];
            _preDelayAt = (_preDelayAt + 1) % preDelayLength;

            float outLeft = 0.0f;
            float outRight = 0.0f;
            for (int comb = 0; comb < CombTuning.Length; comb++)
            {
                outLeft += Comb(comb, into, feedback, damp1, damp2);
                outRight += Comb(comb + CombTuning.Length, into, feedback, damp1, damp2);
            }

            for (int allpass = 0; allpass < AllpassTuning.Length; allpass++)
            {
                outLeft = Allpass(allpass, outLeft);
                outRight = Allpass(allpass + AllpassTuning.Length, outRight);
            }

            float wet1 = WetScale * ((width / 2.0f) + 0.5f);
            float wet2 = WetScale * ((1.0f - width) / 2.0f);
            float reverbLeft = (outLeft * wet1) + (outRight * wet2);
            float reverbRight = (outRight * wet1) + (outLeft * wet2);

            if (channels > 1)
            {
                left[index] = (dryLeft * (1.0f - mix)) + (reverbLeft * mix);
                right[index] = (dryRight * (1.0f - mix)) + (reverbRight * mix);
            }
            else
            {
                left[index] = (dryLeft * (1.0f - mix)) + ((reverbLeft + reverbRight) * 0.5f * mix);
            }
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        foreach (float[] line in _combs)
        {
            Array.Clear(line);
        }

        foreach (float[] line in _allpasses)
        {
            Array.Clear(line);
        }

        Array.Clear(_combAt);
        Array.Clear(_combStore);
        Array.Clear(_allpassAt);
        Array.Clear(_preDelay);
        _preDelayAt = 0;
    }

    /// <inheritdoc />
    protected override void OnPrepare(int sampleRate, int channels)
    {
        double scale = sampleRate / 44100.0;
        _combs = [.. CombTuning.Select(tuning => new float[Math.Max(1, (int)(tuning * scale))]), .. CombTuning.Select(tuning => new float[Math.Max(1, (int)((tuning + Spread) * scale))])];
        _allpasses = [.. AllpassTuning.Select(tuning => new float[Math.Max(1, (int)(tuning * scale))]), .. AllpassTuning.Select(tuning => new float[Math.Max(1, (int)((tuning + Spread) * scale))])];
        _combAt = new int[_combs.Length];
        _combStore = new float[_combs.Length];
        _allpassAt = new int[_allpasses.Length];
        _preDelay = new float[(int)(0.21 * sampleRate) + 1];
        _preDelayAt = 0;
    }

    private float Comb(int comb, float input, float feedback, float damp1, float damp2)
    {
        float[] line = _combs[comb];
        int at = _combAt[comb];
        float output = line[at];
        _combStore[comb] = (output * damp2) + (_combStore[comb] * damp1);
        line[at] = input + (_combStore[comb] * feedback);
        _combAt[comb] = at + 1 == line.Length ? 0 : at + 1;
        return output;
    }

    private float Allpass(int allpass, float input)
    {
        float[] line = _allpasses[allpass];
        int at = _allpassAt[allpass];
        float stored = line[at];
        line[at] = input + (stored * 0.5f);
        _allpassAt[allpass] = at + 1 == line.Length ? 0 : at + 1;
        return stored - input;
    }
}

/// <summary>
/// Echoes: the sound again after a time, fading by the feedback, bouncing between the sides.
/// </summary>
/// <remarks>
/// Each channel has a line up to two seconds long, read between samples so a change of time is a
/// sweep (a tape delay's pitch bend) rather than a jump. Each echo passes a one-pole low pass,
/// so repeats darken as real ones do. Ping-pong, on a stereo track, feeds the sum of the two
/// sides into the left line, the left's echo into the right and the right's back into the left.
/// The echoes are added over the dry sound, which stays at its level.
/// </remarks>
[AudioEffect("audio.delay", Name = "Delay", Category = "Space", Description = "Repeats the sound after a time, each echo quieter and darker, optionally bouncing between left and right.")]
[Param("time", ParamType.Float, Default = "350", Min = 1, Max = 2000, SliderMax = 1000, Unit = "ms", Description = "The time between echoes.")]
[Param("feedback", ParamType.Float, Default = "0.35", Min = 0, Max = 0.95, Description = "How much of each echo comes back again: 0 is one echo, near 1 goes on and on.")]
[Param("mix", ParamType.Float, Default = "0.3", Min = 0, Max = 1, Description = "How loud the echoes are against the dry sound.")]
[Param("ping-pong", ParamType.Bool, Default = "true", Label = "Ping-pong", Description = "Echoes alternate between left and right.")]
[Param("high-cut", ParamType.Float, Default = "8000", Min = 1000, Max = 20000, Unit = "Hz", Label = "High cut", Description = "How dark each echo gets.")]
public sealed class DelayEffect : AudioEffect
{
    private const double MaxSeconds = 2.0;

    private float[][] _lines = [];
    private float[] _damp = [];
    private int _at;

    /// <inheritdoc />
    public override double TailSeconds => 10.0;

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        if (_lines.Length == 0)
        {
            return;
        }

        int frames = block.Frames;
        int channels = Math.Min(block.Channels, Channels);
        int length = _lines[0].Length;
        bool pingPong = block.To[3] >= 0.5f && channels >= 2;
        float damping = 1.0f - MathF.Exp(-2.0f * MathF.PI * block.To[4] / block.SampleRate);

        for (int index = 0; index < frames; index++)
        {
            float t = (float)index / frames;
            float time = Math.Clamp(CompressorEffect.Lerp(block.From[0], block.To[0], t) * 0.001f * block.SampleRate, 1.0f, length - 2);
            float feedback = CompressorEffect.Lerp(block.From[1], block.To[1], t);
            float mix = CompressorEffect.Lerp(block.From[2], block.To[2], t);

            if (pingPong)
            {
                float leftEcho = Read(0, time, length);
                float rightEcho = Read(1, time, length);
                Span<float> left = block.Plane(0);
                Span<float> right = block.Plane(1);
                float input = (left[index] + right[index]) * 0.5f;

                _lines[0][_at] = input + (Darken(1, rightEcho, damping) * feedback);
                _lines[1][_at] = Darken(0, leftEcho, damping) * feedback;
                left[index] += leftEcho * mix;
                right[index] += rightEcho * mix;

                for (int channel = 2; channel < channels; channel++)
                {
                    _lines[channel][_at] = 0.0f;
                }
            }
            else
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    Span<float> plane = block.Plane(channel);
                    float echo = Read(channel, time, length);
                    _lines[channel][_at] = plane[index] + (Darken(channel, echo, damping) * feedback);
                    plane[index] += echo * mix;
                }
            }

            _at = (_at + 1) % length;
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        foreach (float[] line in _lines)
        {
            Array.Clear(line);
        }

        Array.Clear(_damp);
        _at = 0;
    }

    /// <inheritdoc />
    protected override void OnPrepare(int sampleRate, int channels)
    {
        int length = (int)Math.Ceiling(MaxSeconds * sampleRate) + 4;
        _lines = [.. Enumerable.Range(0, channels).Select(_ => new float[length])];
        _damp = new float[channels];
        _at = 0;
    }

    /// <summary>The line <paramref name="delay"/> samples behind the write position, between samples.</summary>
    private float Read(int channel, float delay, int length)
    {
        float[] line = _lines[channel];
        float position = _at - delay;
        if (position < 0.0f)
        {
            position += length;
        }

        int whole = (int)position;
        float fraction = position - whole;
        float a = line[whole % length];
        float b = line[(whole + 1) % length];
        return a + ((b - a) * fraction);
    }

    private float Darken(int channel, float echo, float coefficient)
    {
        _damp[channel] += (echo - _damp[channel]) * coefficient;
        return _damp[channel];
    }
}
