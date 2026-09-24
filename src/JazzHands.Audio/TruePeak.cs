using System.Numerics;

namespace JazzHands.Audio;

/// <summary>
/// Finds the peaks between samples, oversampled eight times, as a true peak meter does.
/// </summary>
/// <remarks>
/// <para>
/// A sample can sit below full scale while the waveform it describes swings above it between two
/// samples, and a converter or a lossy codec reconstructs that swing. So a ceiling in dBTP is on
/// the reconstructed signal: each sample and the seven points an eighth of the way apart to the
/// next, interpolated by a Kaiser windowed sinc 96 taps long. ITU-R BS.1770 asks for at least four
/// times; eight, with a long filter, is what keeps a square wave's aliased harmonics near half the
/// sample rate from slipping between the points (four times and twelve taps read a 5 kHz full
/// scale square 1.4 dB low, and 48 taps still let a naive square's 23 kHz alias through).
/// </para>
/// <para>
/// The interpolation needs half its taps after the point, so a reading is <see cref="Delay"/>
/// samples late: <see cref="Push"/> answers for the sample that far behind the one pushed.
/// </para>
/// </remarks>
public sealed class TruePeak
{
    /// <summary>Samples each interpolated phase reads.</summary>
    public const int Taps = 96;

    /// <summary>How far behind the newest sample a reading is.</summary>
    public const int Delay = Taps / 2;

    /// <summary>Points per sample, the sample itself included.</summary>
    public const int Factor = 8;

    private static readonly float[][] Phases = Design();

    private readonly float[] _history;
    private int _at;

    /// <summary>Creates a detector for one channel.</summary>
    public TruePeak() => _history = new float[Taps * 2];

    /// <summary>
    /// Takes the next sample and returns the largest absolute value of the reconstructed signal
    /// from the sample <see cref="Delay"/> ago up to the one after it.
    /// </summary>
    public float Push(float sample)
    {
        // Written twice, a ring's length apart, so the last Taps samples are always one run.
        _history[_at] = sample;
        _history[_at + Taps] = sample;
        _at = (_at + 1) % Taps;

        ReadOnlySpan<float> window = _history.AsSpan(_at, Taps);
        float peak = Math.Abs(window[Delay - 1]);

        int width = Vector<float>.Count;
        foreach (float[] phase in Phases)
        {
            // Taps is a multiple of every vector width, so there is no remainder.
            Vector<float> sum = Vector<float>.Zero;
            for (int tap = 0; tap < Taps; tap += width)
            {
                sum += new Vector<float>(phase, tap) * new Vector<float>(window[tap..]);
            }

            peak = Math.Max(peak, Math.Abs(Vector.Sum(sum)));
        }

        return peak;
    }

    /// <summary>Forgets the signal so far.</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _at = 0;
    }

    /// <summary>The true peak of a run of samples, in linear terms, for a test or an analysis.</summary>
    public static float Of(ReadOnlySpan<float> samples)
    {
        var detector = new TruePeak();
        float peak = 0.0f;
        foreach (float sample in samples)
        {
            peak = Math.Max(peak, detector.Push(sample));
        }

        for (int flush = 0; flush < Taps; flush++)
        {
            peak = Math.Max(peak, detector.Push(0.0f));
        }

        return peak;
    }

    /// <summary>
    /// The seven fractional phases: the point p/8 of the way from window[Delay - 1] to
    /// window[Delay]. A Kaiser window (beta 8) over the filter's length.
    /// </summary>
    private static float[][] Design()
    {
        const double Beta = 8.0;
        double norm = BesselI0(Beta);
        var phases = new float[Factor - 1][];
        for (int p = 1; p < Factor; p++)
        {
            double fraction = (double)p / Factor;
            var taps = new float[Taps];
            double sum = 0.0;
            for (int tap = 0; tap < Taps; tap++)
            {
                double t = (tap - (Delay - 1)) - fraction;
                double sinc = Math.Abs(t) < 1e-12 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
                double x = t / (Delay + 0.5);
                double window = Math.Abs(x) >= 1.0 ? 0.0 : BesselI0(Beta * Math.Sqrt(1.0 - (x * x))) / norm;
                taps[tap] = (float)(sinc * window);
                sum += sinc * window;
            }

            // Unity gain at DC, so a constant reads as itself between samples.
            for (int tap = 0; tap < Taps; tap++)
            {
                taps[tap] = (float)(taps[tap] / sum);
            }

            phases[p - 1] = taps;
        }

        return phases;
    }

    /// <summary>The zeroth order modified Bessel function of the first kind, by its series.</summary>
    private static double BesselI0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        for (int k = 1; k < 50; k++)
        {
            term *= (x / (2.0 * k)) * (x / (2.0 * k));
            sum += term;
            if (term < 1e-12 * sum)
            {
                break;
            }
        }

        return sum;
    }
}
