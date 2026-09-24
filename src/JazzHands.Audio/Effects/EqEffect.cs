using JazzHands.Core.Effects;

namespace JazzHands.Audio.Effects;

/// <summary>
/// Eight band parametric EQ: a low cut, a low shelf, four bells, a high shelf and a high cut.
/// </summary>
/// <remarks>
/// Fixed band types rather than eight bands of any type, which is how the EQs in editors people
/// already know are laid out and what a sentence can describe ("cut the rumble, a little air on
/// top"); the skill's eight bands are all here. Each band is an RBJ biquad
/// (<see cref="Biquad"/>). A band at 0 dB, or a cut that is off, is skipped. While a setting
/// moves, coefficients are made again every 32 samples along the ramp, which keeps a sweep from
/// zipping; a still EQ makes them once and keeps them.
/// </remarks>
[AudioEffect("audio.eq.parametric", Name = "Parametric EQ", Category = "EQ", Description = "Shapes the tone: a low cut, a low shelf, four bells, a high shelf and a high cut.")]
[Param("low-cut", ParamType.Bool, Default = "false", Label = "Low cut", Description = "Takes away everything below its frequency, 12 dB an octave: rumble, handling noise, wind.")]
[Param("low-cut-freq", ParamType.Float, Default = "80", Min = 20, Max = 1000, Unit = "Hz", Label = "Low cut at", Description = "Where the low cut starts.")]
[Param("low-shelf-freq", ParamType.Float, Default = "120", Min = 20, Max = 2000, Unit = "Hz", Label = "Low shelf at", Description = "Where the low shelf turns.")]
[Param("low-shelf-gain", ParamType.Float, Default = "0", Min = -24, Max = 24, Unit = "dB", Label = "Low shelf", Description = "More or less of everything under the low shelf's frequency.")]
[Param("band-1-freq", ParamType.Float, Default = "250", Min = 20, Max = 20000, Unit = "Hz", Label = "Band 1 at", Description = "The first bell's centre.")]
[Param("band-1-gain", ParamType.Float, Default = "0", Min = -24, Max = 24, Unit = "dB", Label = "Band 1", Description = "The first bell's boost or cut.")]
[Param("band-1-q", ParamType.Float, Default = "1", Min = 0.1, Max = 18, SliderMax = 10, Label = "Band 1 Q", Description = "The first bell's width: higher is narrower.")]
[Param("band-2-freq", ParamType.Float, Default = "800", Min = 20, Max = 20000, Unit = "Hz", Label = "Band 2 at", Description = "The second bell's centre.")]
[Param("band-2-gain", ParamType.Float, Default = "0", Min = -24, Max = 24, Unit = "dB", Label = "Band 2", Description = "The second bell's boost or cut.")]
[Param("band-2-q", ParamType.Float, Default = "1", Min = 0.1, Max = 18, SliderMax = 10, Label = "Band 2 Q", Description = "The second bell's width.")]
[Param("band-3-freq", ParamType.Float, Default = "2500", Min = 20, Max = 20000, Unit = "Hz", Label = "Band 3 at", Description = "The third bell's centre.")]
[Param("band-3-gain", ParamType.Float, Default = "0", Min = -24, Max = 24, Unit = "dB", Label = "Band 3", Description = "The third bell's boost or cut.")]
[Param("band-3-q", ParamType.Float, Default = "1", Min = 0.1, Max = 18, SliderMax = 10, Label = "Band 3 Q", Description = "The third bell's width.")]
[Param("band-4-freq", ParamType.Float, Default = "6000", Min = 20, Max = 20000, Unit = "Hz", Label = "Band 4 at", Description = "The fourth bell's centre.")]
[Param("band-4-gain", ParamType.Float, Default = "0", Min = -24, Max = 24, Unit = "dB", Label = "Band 4", Description = "The fourth bell's boost or cut.")]
[Param("band-4-q", ParamType.Float, Default = "1", Min = 0.1, Max = 18, SliderMax = 10, Label = "Band 4 Q", Description = "The fourth bell's width.")]
[Param("high-shelf-freq", ParamType.Float, Default = "8000", Min = 1000, Max = 20000, Unit = "Hz", Label = "High shelf at", Description = "Where the high shelf turns.")]
[Param("high-shelf-gain", ParamType.Float, Default = "0", Min = -24, Max = 24, Unit = "dB", Label = "High shelf", Description = "More or less of everything over the high shelf's frequency: air, or harshness taken off.")]
[Param("high-cut", ParamType.Bool, Default = "false", Label = "High cut", Description = "Takes away everything above its frequency, 12 dB an octave: hiss, or a telephone.")]
[Param("high-cut-freq", ParamType.Float, Default = "16000", Min = 1000, Max = 20000, Unit = "Hz", Label = "High cut at", Description = "Where the high cut starts.")]
public sealed class ParametricEqEffect : AudioEffect
{
    /// <summary>The bands, in the order they run.</summary>
    public const int Bands = 8;

    private const int Step = 32;

    private readonly float[] _values = new float[20];
    private readonly float[] _built = new float[20];
    private readonly Biquad[] _design = new Biquad[Bands];
    private readonly bool[] _active = new bool[Bands];
    private Biquad[] _filters = [];
    private bool _designed;

    /// <summary>The filter a band is at settings given in declared parameter order, for tests and a curve display.</summary>
    public static Biquad Design(int band, ReadOnlySpan<float> values, double sampleRate, out bool active)
    {
        switch (band)
        {
            case 0:
                active = values[0] >= 0.5f;
                return active ? Biquad.HighPass(sampleRate, values[1]) : Biquad.Identity;
            case 1:
                active = MathF.Abs(values[3]) > 0.001f;
                return active ? Biquad.LowShelf(sampleRate, values[2], values[3]) : Biquad.Identity;
            case >= 2 and <= 5:
                int at = 4 + ((band - 2) * 3);
                active = MathF.Abs(values[at + 1]) > 0.001f;
                return active ? Biquad.Peak(sampleRate, values[at], values[at + 1], values[at + 2]) : Biquad.Identity;
            case 6:
                active = MathF.Abs(values[17]) > 0.001f;
                return active ? Biquad.HighShelf(sampleRate, values[16], values[17]) : Biquad.Identity;
            default:
                active = values[18] >= 0.5f;
                return active ? Biquad.LowPass(sampleRate, values[19]) : Biquad.Identity;
        }
    }

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        int frames = block.Frames;
        int channels = Math.Min(block.Channels, Channels);
        bool moving = !block.From.SequenceEqual(block.To);

        for (int start = 0; start < frames; start += Step)
        {
            int count = Math.Min(Step, frames - start);

            // The settings at the middle of this step, along the block's ramp.
            float t = moving ? (start + (count / 2.0f)) / frames : 0.0f;
            for (int index = 0; index < _values.Length; index++)
            {
                _values[index] = block.From[index] + ((block.To[index] - block.From[index]) * t);
            }

            Redesign(block.SampleRate);

            for (int channel = 0; channel < channels; channel++)
            {
                Span<float> samples = block.Plane(channel).Slice(start, count);
                for (int band = 0; band < Bands; band++)
                {
                    if (!_active[band])
                    {
                        continue;
                    }

                    ref Biquad filter = ref _filters[(channel * Bands) + band];
                    for (int index = 0; index < samples.Length; index++)
                    {
                        samples[index] = filter.Process(samples[index]);
                    }
                }
            }

            if (!moving)
            {
                // The same settings for the rest of the block.
                for (int from = start + count; from < frames; from += Step)
                {
                    int rest = Math.Min(Step, frames - from);
                    for (int channel = 0; channel < channels; channel++)
                    {
                        Span<float> samples = block.Plane(channel).Slice(from, rest);
                        for (int band = 0; band < Bands; band++)
                        {
                            if (!_active[band])
                            {
                                continue;
                            }

                            ref Biquad filter = ref _filters[(channel * Bands) + band];
                            for (int index = 0; index < samples.Length; index++)
                            {
                                samples[index] = filter.Process(samples[index]);
                            }
                        }
                    }
                }

                break;
            }
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        for (int index = 0; index < _filters.Length; index++)
        {
            _filters[index].Reset();
        }
    }

    /// <inheritdoc />
    protected override void OnPrepare(int sampleRate, int channels)
    {
        _filters = new Biquad[channels * Bands];
        Array.Fill(_filters, Biquad.Identity);
        _designed = false;
    }

    /// <summary>Makes the coefficients again when the settings have changed, keeping each filter's state.</summary>
    private void Redesign(int sampleRate)
    {
        if (_designed && _values.AsSpan().SequenceEqual(_built))
        {
            return;
        }

        _values.CopyTo(_built, 0);
        _designed = true;
        for (int band = 0; band < Bands; band++)
        {
            bool was = _active[band];
            _design[band] = Design(band, _values, sampleRate, out _active[band]);
            for (int channel = 0; channel * Bands < _filters.Length; channel++)
            {
                ref Biquad filter = ref _filters[(channel * Bands) + band];
                if (!was && _active[band])
                {
                    // A band coming in starts clean rather than from whatever it last held.
                    filter.Reset();
                }

                filter.Take(_design[band]);
            }
        }
    }
}
