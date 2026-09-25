using JazzHands.Core.Effects;

namespace JazzHands.Audio.Effects;

/// <summary>
/// The running half of a sound generator: a clip on a sound track that makes its sound rather than
/// reading a file.
/// </summary>
/// <remarks>
/// A class with <see cref="AudioGeneratorAttribute"/> and its <see cref="ParamAttribute"/>s. It
/// writes the mix's channels itself (so a level means that level in each channel, and a channel
/// choice lands where it says), from the clip's own sample position alone, so any moment plays
/// the same whether it is reached by playing or by a jump, and two exports are one file. Made per
/// clip by the mix builder with the clip's parameters as they are at its start; the audio thread
/// only calls <see cref="Fill"/>, which must not allocate.
/// </remarks>
public abstract class AudioGenerator
{
    /// <summary>Which channels a generator sounds in.</summary>
    protected const int All = 0;

    /// <summary>Takes the clip's parameters, by name, as numbers (an enum as its choice's index).</summary>
    public abstract void Configure(IReadOnlyDictionary<string, float> parameters, int sampleRate);

    /// <summary>Writes the sound of a run of the clip's samples into each of the mix's channels.</summary>
    /// <param name="buffer">Where to write, one plane per mix channel.</param>
    /// <param name="channels">How many channels the mix has.</param>
    /// <param name="position">The clip's sample the run starts at, from its start in its own time.</param>
    /// <param name="frames">How many samples.</param>
    public abstract void Fill(AudioBuffer buffer, int channels, long position, int frames);

    /// <summary>
    /// The mix channel a channel choice names (all, left, right, centre, lfe, left surround,
    /// right surround), or -1 for every channel. On a stereo mix centre and the surrounds are both
    /// sides, and the LFE nothing.
    /// </summary>
    protected static bool Sounds(int choice, int channel, int channels) => choice switch
    {
        All => true,
        1 => channel == 0,
        2 => channel == 1,
        3 => channels == 6 ? channel == 2 : channel < 2,
        4 => channels == 6 && channel == 3,
        5 => channels == 6 ? channel == 4 : channel == 0,
        6 => channels == 6 ? channel == 5 : channel == 1,
        _ => true,
    };
}

/// <summary>A steady sine for lining up levels: 1 kHz at -18 dBFS by default, in every channel or one.</summary>
[AudioGenerator("audio.gen.tone", Name = "Test Tone", Category = "Calibration", Description = "A steady sine for lining up levels: 1 kHz at -18 dBFS in every channel, or in one to check where each speaker is.")]
[Param("frequency", ParamType.Float, Default = "1000", Min = 20, Max = 20000, SliderMax = 10000, Unit = "Hz", Animatable = false, Description = "The pitch.")]
[Param("level", ParamType.Float, Default = "-18", Min = -60, Max = 0, Unit = "dBFS", Animatable = false, Description = "How loud its peaks are in each channel it sounds in.")]
[Param("channel", ParamType.Enum, Default = "all", Choices = "all, left, right, centre, lfe, left-surround, right-surround", Animatable = false, Description = "Which channel it sounds in; on a stereo mix centre is both sides and the LFE silent.")]
public sealed class ToneGenerator : AudioGenerator
{
    private double _step;
    private float _amplitude;
    private int _channel;

    /// <inheritdoc />
    public override void Configure(IReadOnlyDictionary<string, float> parameters, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _step = 2 * Math.PI * parameters["frequency"] / sampleRate;
        _amplitude = Dsp.DbToGain(parameters["level"]);
        _channel = (int)parameters["channel"];
    }

    /// <inheritdoc />
    public override void Fill(AudioBuffer buffer, int channels, long position, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (int channel = 0; channel < channels; channel++)
        {
            Span<float> plane = buffer.Plane(channel, 0, frames);
            if (!Sounds(_channel, channel, channels))
            {
                plane.Clear();
                continue;
            }

            for (int index = 0; index < frames; index++)
            {
                // The phase from the position alone, reduced to a cycle first so an hour in is as exact as the start.
                double phase = Math.IEEERemainder((position + index) * _step, 2 * Math.PI);
                plane[index] = _amplitude * (float)Math.Sin(phase);
            }
        }
    }
}

/// <summary>Pink noise for lining up levels and checking speakers: equal power in every octave, -20 dBFS RMS by default.</summary>
/// <remarks>
/// Voss-McCartney: sixteen random rows, the k-th changing every 2^k samples, and white noise, added.
/// Each row's value at each change is a hash of the row, the change's number and the channel, so
/// the noise is a function of the position and every channel's is its own.
/// </remarks>
[AudioGenerator("audio.gen.pink-noise", Name = "Pink Noise", Category = "Calibration", Description = "Pink noise, equal power in every octave, for lining up levels and checking speakers: -20 dBFS RMS in every channel, each its own, or in one.")]
[Param("level", ParamType.Float, Default = "-20", Min = -60, Max = 0, Unit = "dBFS", Animatable = false, Description = "Its RMS level in each channel it sounds in.")]
[Param("channel", ParamType.Enum, Default = "all", Choices = "all, left, right, centre, lfe, left-surround, right-surround", Animatable = false, Description = "Which channel it sounds in; on a stereo mix centre is both sides and the LFE silent.")]
public sealed class PinkNoiseGenerator : AudioGenerator
{
    private const int Rows = 16;

    /// <summary>The RMS of the sum of the rows and the white noise, each uniform from -1 to 1.</summary>
    private static readonly float Rms = MathF.Sqrt((Rows + 1) / 3.0f);

    private float _scale;
    private int _channel;

    /// <inheritdoc />
    public override void Configure(IReadOnlyDictionary<string, float> parameters, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _scale = Dsp.DbToGain(parameters["level"]) / Rms;
        _channel = (int)parameters["channel"];
    }

    /// <inheritdoc />
    public override void Fill(AudioBuffer buffer, int channels, long position, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (int channel = 0; channel < channels; channel++)
        {
            Span<float> plane = buffer.Plane(channel, 0, frames);
            if (!Sounds(_channel, channel, channels))
            {
                plane.Clear();
                continue;
            }

            for (int index = 0; index < frames; index++)
            {
                long at = position + index;
                float sum = Uniform((ulong)at, Rows, channel);
                for (int row = 0; row < Rows; row++)
                {
                    sum += Uniform((ulong)(at >> row), row, channel);
                }

                plane[index] = sum * _scale;
            }
        }
    }

    /// <summary>A value from -1 to 1 from a position, a row and a channel: SplitMix64's finaliser.</summary>
    private static float Uniform(ulong index, int row, int channel)
    {
        ulong x = index + ((ulong)row * 0x9E3779B97F4A7C15UL) + ((ulong)channel * 0xD1B54A32D192ED03UL);
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return ((x >> 40) / (float)(1UL << 23)) - 1.0f;
    }
}
