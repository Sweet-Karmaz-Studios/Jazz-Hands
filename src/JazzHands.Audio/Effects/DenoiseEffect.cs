using JazzHands.Audio.Analysis;
using JazzHands.Core.Effects;

namespace JazzHands.Audio.Effects;

/// <summary>
/// Takes steady background noise out of a recording: fans, hiss, hum, a room.
/// </summary>
/// <remarks>
/// <para>
/// A spectral gate. The sound is cut into 1024 sample frames every 256, windowed and transformed;
/// each frequency is turned down by how little it stands over the noise there (power subtraction,
/// <c>sensitivity</c> squared times the noise), never by more than <c>reduction</c>; the gains are
/// smoothed from one frame to the next, which keeps the leftover noise from warbling (musical
/// noise); and the frames are transformed back and overlapped. The noise is the profile
/// <c>audio.learn-noise</c> measured from a stretch of only noise, or without one, each
/// frequency's own quietest level, followed slowly upwards.
/// </para>
/// <para>
/// A clip effect with 1024 samples of latency (21 ms at 48 kHz): the mixer reads the clip's file that
/// far ahead, so the clip stays in time. Turn it off and on in the Inspector to hear what it takes.
/// </para>
/// </remarks>
[AudioEffect("audio.denoise", Name = "Noise Reduction", Category = "Restoration", Description = "Takes steady background noise out of a clip (a fan, hiss, hum, the room): learn the noise from a quiet stretch with audio.learn-noise, or let it follow the quietest level by itself.")]
[Param("profile", ParamType.Text, Default = "", Animatable = false, Description = "The noise to take out, learned by audio.learn-noise from a stretch of only noise: a level in dB per sixth of an octave from 20 Hz. Empty follows each frequency's quietest level by itself.")]
[Param("reduction", ParamType.Float, Default = "12", Min = 0, Max = 40, Unit = "dB", Description = "How far the noise is turned down, at most.")]
[Param("sensitivity", ParamType.Float, Default = "2", Min = 0.5, Max = 4, Description = "How far over the noise a sound must be to be kept: higher takes more out, and risks quiet sound with it.")]
[Param("smoothing", ParamType.Float, Default = "0.5", Min = 0, Max = 0.95, Description = "How slowly the reduction changes from moment to moment: higher keeps the leftover noise from warbling.")]
public sealed class DenoiseEffect : AudioEffect, IClipEffect
{
    /// <summary>Samples between frames.</summary>
    public const int Hop = NoiseProfile.FrameSize / 4;

    private const int Size = NoiseProfile.FrameSize;
    private const int Bins = (Size / 2) + 1;
    private const int Reduction = 1;
    private const int Sensitivity = 2;
    private const int Smoothing = 3;

    /// <summary>How fast the self-followed noise floor may rise, per frame: about 4 dB a second at 48 kHz.</summary>
    private const double FloorRise = 1.005;

    /// <summary>
    /// How much of the last frame's power a frequency's level keeps when deciding its gain: a
    /// little memory evens out the random swings of noise, which would otherwise slip through.
    /// </summary>
    private const double Memory = 0.5;

    /// <summary>
    /// The self-followed floor follows the quietest of a longer average (about ten frames), and the
    /// noise is that times this: the quietest of a noise's swings sits under its average.
    /// </summary>
    private const double LongMemory = 0.9;

    private const double FloorBias = 1.6;

    private readonly Fft _fft = new(Size);
    private readonly double[] _window = [.. NoiseProfile.Window];
    private readonly double[] _real = new double[Size];
    private readonly double[] _imaginary = new double[Size];
    private Channel[] _channels = [];
    private double[]? _bands;
    private double[]? _noise;
    private int _rover = Size - Hop;

    /// <inheritdoc />
    public override int LatencySamples => Size;

    /// <inheritdoc />
    public override void Text(string name, string value)
    {
        if (name != "profile")
        {
            return;
        }

        // Parsed here, on the building thread; the audio thread picks up the new bins by one reference.
        double[]? bands = NoiseProfile.Parse(value);
        _noise = bands is null || SampleRate <= 0 ? null : NoiseProfile.Bins(bands, SampleRate);
        _bands = bands;
    }

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        int frames = block.Frames;
        int channels = Math.Min(block.Channels, _channels.Length);
        double reduction = Dsp.DbToGain(-block.To[Reduction]);
        double sensitivity = block.To[Sensitivity] * block.To[Sensitivity];
        double smoothing = block.To[Smoothing];
        double[]? noise = Volatile.Read(ref _noise);

        int rover = _rover;
        for (int channel = 0; channel < channels; channel++)
        {
            Span<float> samples = block.Plane(channel);
            Channel state = _channels[channel];
            rover = _rover;
            for (int index = 0; index < frames; index++)
            {
                state.Input[rover] = samples[index];
                samples[index] = (float)state.Output[rover - (Size - Hop)];
                rover++;

                if (rover == Size)
                {
                    rover = Size - Hop;
                    Frame(state, noise, reduction, sensitivity, smoothing);
                }
            }
        }

        _rover = rover;
    }

    /// <inheritdoc />
    public override void Reset()
    {
        foreach (Channel channel in _channels)
        {
            channel.Clear();
        }

        _rover = Size - Hop;
    }

    /// <inheritdoc />
    protected override void OnPrepare(int sampleRate, int channels)
    {
        _channels = [.. Enumerable.Range(0, channels).Select(_ => new Channel())];
        _rover = Size - Hop;
        if (_bands is { } bands)
        {
            _noise = NoiseProfile.Bins(bands, sampleRate);
        }
    }

    /// <summary>One frame: windowed, transformed, each frequency gated, back, and overlapped into the output.</summary>
    private void Frame(Channel state, double[]? profile, double reduction, double sensitivity, double smoothing)
    {
        for (int index = 0; index < Size; index++)
        {
            _real[index] = state.Input[index] * _window[index];
            _imaginary[index] = 0;
        }

        _fft.Forward(_real, _imaginary);

        for (int bin = 0; bin < Bins; bin++)
        {
            double raw = (_real[bin] * _real[bin]) + (_imaginary[bin] * _imaginary[bin]);
            double power = state.Started ? (Memory * state.Power[bin]) + ((1 - Memory) * raw) : raw;
            state.Power[bin] = power;
            double floor;
            if (profile is not null)
            {
                floor = profile[bin];
            }
            else
            {
                // Follows the quietest the frequency's longer average has been, down at once and up slowly.
                double average = state.Started ? (LongMemory * state.Average[bin]) + ((1 - LongMemory) * raw) : raw;
                state.Average[bin] = average;
                state.Floor[bin] = average < state.Floor[bin] ? average : state.Floor[bin] * FloorRise;
                floor = state.Floor[bin] * FloorBias;
            }

            double gain = power > 0 ? Math.Sqrt(Math.Max(0, 1 - (sensitivity * floor / power))) : 0;
            gain = Math.Max(gain, reduction);
            gain = (smoothing * state.Gains[bin]) + ((1 - smoothing) * gain);
            state.Gains[bin] = gain;

            _real[bin] *= gain;
            _imaginary[bin] *= gain;
            if (bin > 0 && bin < Size / 2)
            {
                _real[Size - bin] *= gain;
                _imaginary[Size - bin] *= gain;
            }
        }

        state.Started = true;
        _fft.Inverse(_real, _imaginary);

        // A square root Hann in and out makes a Hann, and four of those a hop apart sum to two.
        for (int index = 0; index < Size; index++)
        {
            state.Sum[index] += _real[index] * _window[index] * 0.5;
        }

        Array.Copy(state.Sum, state.Output, Hop);
        Array.Copy(state.Sum, Hop, state.Sum, 0, Size - Hop);
        Array.Clear(state.Sum, Size - Hop, Hop);
        Array.Copy(state.Input, Hop, state.Input, 0, Size - Hop);
    }

    /// <summary>One channel's frames in and out, its gains and its noise floor.</summary>
    private sealed class Channel
    {
        public double[] Input { get; } = new double[Size];

        public double[] Output { get; } = new double[Hop];

        public double[] Sum { get; } = new double[Size];

        public double[] Gains { get; } = new double[Bins];

        public double[] Floor { get; } = new double[Bins];

        public double[] Power { get; } = new double[Bins];

        public double[] Average { get; } = new double[Bins];

        public bool Started { get; set; }

        public Channel() => Clear();

        public void Clear()
        {
            Array.Clear(Input);
            Array.Clear(Output);
            Array.Clear(Sum);
            Array.Fill(Gains, 1.0);
            Array.Fill(Floor, double.MaxValue);
            Array.Clear(Power);
            Array.Clear(Average);
            Started = false;
        }
    }
}
