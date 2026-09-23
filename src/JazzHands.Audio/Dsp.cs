using JazzHands.Core.Model;

namespace JazzHands.Audio;

/// <summary>
/// The stateless arithmetic the graph is built from: decibels, pan laws, fade shapes and the
/// channel matrix.
/// </summary>
/// <remarks>
/// Everything here is a pure function of its arguments and allocates nothing, so any of it can
/// be called from the audio thread. The matrix is written into a caller's span, which is how a
/// block gets two of them from <c>stackalloc</c> rather than from the heap.
/// </remarks>
public static class Dsp
{
    /// <summary>The level treated as silence. Anything at or below it is a gain of exactly zero.</summary>
    public const float SilenceDb = -144.0f;

    /// <summary>
    /// The most channels a source or a mix may have. Enough for 7.1; the matrix is sized by it.
    /// </summary>
    public const int MaxChannels = 8;

    /// <summary>
    /// Centre and surrounds are folded into stereo at -3 dB, as ITU-R BS.775 has it.
    /// </summary>
    public const float DownmixCoefficient = 0.70710677f;

    /// <summary>Division that rounds towards negative infinity, for sample positions before zero.</summary>
    public static long FloorDiv(long value, long divisor)
    {
        long quotient = value / divisor;
        return (value % divisor != 0) && ((value < 0) ^ (divisor < 0)) ? quotient - 1 : quotient;
    }

    /// <summary>Converts decibels to a linear gain.</summary>
    public static float DbToGain(float db) => db <= SilenceDb ? 0.0f : MathF.Pow(10.0f, db / 20.0f);

    /// <summary>Converts a linear gain to decibels, floored at <see cref="SilenceDb"/>.</summary>
    public static float GainToDb(float gain) => gain <= 0.0f ? SilenceDb : Math.Max(SilenceDb, 20.0f * MathF.Log10(gain));

    /// <summary>
    /// Constant power pan of a mono signal: equal loudness wherever it sits.
    /// </summary>
    /// <remarks>
    /// Centre is -3 dB in each side, so the two sides sum to the same power as one side at full
    /// scale. A linear pan would dip by 3 dB in the middle, which is what makes a sound panned
    /// across the stereo field seem to fall back and then come forward again.
    /// </remarks>
    /// <param name="pan">-1 is hard left, 0 centre, 1 hard right. Clamped.</param>
    /// <param name="left">The gain for the left side.</param>
    /// <param name="right">The gain for the right side.</param>
    public static void ConstantPowerPan(float pan, out float left, out float right)
    {
        float angle = (Math.Clamp(pan, -1.0f, 1.0f) + 1.0f) * MathF.PI / 4.0f;
        left = MathF.Cos(angle);
        right = MathF.Sin(angle);
    }

    /// <summary>
    /// Balance of a signal that already has two sides: centre leaves both alone.
    /// </summary>
    /// <remarks>
    /// Panning stereo is attenuating the far side, not moving a point, so centre has to be unity
    /// or every stereo clip would lose 3 dB the moment it was put on a timeline. The side being
    /// turned down follows a quarter cosine, so it is -3 dB halfway and gone at the end.
    /// </remarks>
    public static void Balance(float pan, out float left, out float right)
    {
        float clamped = Math.Clamp(pan, -1.0f, 1.0f);
        left = clamped > 0.0f ? MathF.Cos(clamped * MathF.PI / 2.0f) : 1.0f;
        right = clamped < 0.0f ? MathF.Cos(-clamped * MathF.PI / 2.0f) : 1.0f;
    }

    /// <summary>
    /// The gain of a fade at a point through it.
    /// </summary>
    /// <remarks>
    /// The model stores a fade's shape as an interpolation mode, so the five shapes are read off
    /// the modes that mean the same thing: linear is linear, ease in and out is the smooth raised
    /// cosine, ease in starts slowly, ease out starts quickly, and a bezier is the steeper S
    /// curve. Hold is a fade that does nothing until its end, which is what holding a value means.
    /// </remarks>
    /// <param name="curve">The shape.</param>
    /// <param name="progress">0 at silence, 1 at full level. Clamped.</param>
    public static float FadeGain(Interp curve, float progress)
    {
        float x = Math.Clamp(progress, 0.0f, 1.0f);

        return curve switch
        {
            Interp.Hold => x >= 1.0f ? 1.0f : 0.0f,
            Interp.EaseInOut => 0.5f - (0.5f * MathF.Cos(MathF.PI * x)),
            Interp.EaseIn => x * x,
            Interp.EaseOut => 1.0f - ((1.0f - x) * (1.0f - x)),
            Interp.Bezier => x * x * x * ((x * ((x * 6.0f) - 15.0f)) + 10.0f),
            _ => x,
        };
    }

    /// <summary>
    /// Writes the matrix that takes a source's channels to the mix's, with a pan applied.
    /// </summary>
    /// <remarks>
    /// Entry <c>[output * sourceChannels + input]</c> is how much of source channel <c>input</c>
    /// goes to mix channel <c>output</c>. The channel order is FFmpeg's default for each count,
    /// which for six is L R C LFE Ls Rs.
    ///
    /// Mono is panned with the constant power law. Stereo is balanced. 5.1 into stereo is the
    /// ITU fold down, with the low frequency channel dropped: it is there for a subwoofer and
    /// would only muddy two speakers. Any other count maps channel for channel as far as both
    /// go, which is right for stereo into 5.1 and harmless for anything exotic.
    /// </remarks>
    /// <param name="sourceChannels">How many channels the source has.</param>
    /// <param name="outputChannels">How many the mix has: 1, 2 or 6.</param>
    /// <param name="pan">-1 to 1.</param>
    /// <param name="matrix">At least <c>sourceChannels * outputChannels</c> long. Overwritten.</param>
    public static void ChannelMatrix(int sourceChannels, int outputChannels, float pan, Span<float> matrix)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceChannels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputChannels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceChannels, MaxChannels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(outputChannels, MaxChannels);
        ArgumentOutOfRangeException.ThrowIfLessThan(matrix.Length, sourceChannels * outputChannels);

        matrix[..(sourceChannels * outputChannels)].Clear();

        if (outputChannels == 1)
        {
            // Fold to stereo and average the two sides, so a mono mix of a stereo source is its
            // centre rather than twice as loud.
            Span<float> stereo = stackalloc float[2 * MaxChannels];
            ChannelMatrix(sourceChannels, 2, pan, stereo);

            for (int input = 0; input < sourceChannels; input++)
            {
                matrix[input] = 0.5f * (stereo[input] + stereo[sourceChannels + input]);
            }

            return;
        }

        if (sourceChannels == 1)
        {
            ConstantPowerPan(pan, out float left, out float right);
            matrix[0] = left;
            matrix[1] = right;
            return;
        }

        Balance(pan, out float leftGain, out float rightGain);

        if (sourceChannels == 6 && outputChannels == 2)
        {
            const int L = 0, R = 1, C = 2, Ls = 4, Rs = 5;

            matrix[L] = leftGain;
            matrix[C] = DownmixCoefficient * leftGain;
            matrix[Ls] = DownmixCoefficient * leftGain;

            matrix[sourceChannels + R] = rightGain;
            matrix[sourceChannels + C] = DownmixCoefficient * rightGain;
            matrix[sourceChannels + Rs] = DownmixCoefficient * rightGain;
            return;
        }

        int shared = Math.Min(sourceChannels, outputChannels);
        for (int channel = 0; channel < shared; channel++)
        {
            matrix[(channel * sourceChannels) + channel] = SideGain(channel, outputChannels, leftGain, rightGain);
        }
    }

    /// <summary>
    /// The channel matrix for a clip that plays only some of its source's channels.
    /// </summary>
    /// <remarks>
    /// Left and right take one channel as a mono signal; mono averages them all, which keeps a
    /// signal that is the same on both sides at the level it was rather than doubling it. The
    /// mono signal is then panned like any mono source. A source that is already mono has
    /// nothing to choose between, so every map plays it as it is.
    /// </remarks>
    public static void ChannelMatrix(int sourceChannels, int outputChannels, float pan, AudioChannelMap map, Span<float> matrix)
    {
        if (map == AudioChannelMap.Auto || sourceChannels == 1)
        {
            ChannelMatrix(sourceChannels, outputChannels, pan, matrix);
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(matrix.Length, sourceChannels * outputChannels);

        Span<float> mono = stackalloc float[MaxChannels];
        ChannelMatrix(1, outputChannels, pan, mono);
        matrix[..(sourceChannels * outputChannels)].Clear();

        for (int output = 0; output < outputChannels; output++)
        {
            int row = output * sourceChannels;

            switch (map)
            {
                case AudioChannelMap.Left:
                    matrix[row] = mono[output];
                    break;

                case AudioChannelMap.Right:
                    matrix[row + 1] = mono[output];
                    break;

                default:
                    for (int input = 0; input < sourceChannels; input++)
                    {
                        matrix[row + input] = mono[output] / sourceChannels;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Which side of a balance one channel of a mix takes, as a track's pan applies to its bus.
    /// </summary>
    public static float SideGain(int channel, int channels, float leftGain, float rightGain)
    {
        // Mono has no sides. In stereo and 5.1 the even front and surround channels are left and
        // the odd ones right; centre and low frequency stay where they are.
        if (channels == 1)
        {
            return 1.0f;
        }

        if (channels == 6)
        {
            return channel switch
            {
                0 or 4 => leftGain,
                1 or 5 => rightGain,
                _ => 1.0f,
            };
        }

        return channel switch
        {
            0 => leftGain,
            1 => rightGain,
            _ => 1.0f,
        };
    }
}
