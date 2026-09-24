namespace JazzHands.Audio;

/// <summary>
/// Finds the peaks between samples, four times oversampled, as ITU-R BS.1770 measures true peak.
/// </summary>
/// <remarks>
/// A sample can sit below full scale while the waveform it describes swings above it between two
/// samples, and a converter or a lossy codec reconstructs that swing. So a ceiling in dBTP is on
/// the reconstructed signal: each sample and the three points a quarter, a half and three quarters
/// of the way to the next, interpolated by a windowed sinc twelve taps a phase. The interpolation
/// needs six samples after the point, so a reading is six samples late: <see cref="Push"/> answers
/// for the sample <see cref="Delay"/> behind the one pushed.
/// </remarks>
public sealed class TruePeak
{
    /// <summary>Samples each interpolated phase reads.</summary>
    public const int Taps = 12;

    /// <summary>How far behind the newest sample a reading is.</summary>
    public const int Delay = Taps / 2;

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
        // Written twice, a ring's length apart, so the last twelve are always one run in memory.
        _history[_at] = sample;
        _history[_at + Taps] = sample;
        _at = (_at + 1) % Taps;

        ReadOnlySpan<float> window = _history.AsSpan(_at, Taps);
        float peak = Math.Abs(window[Delay - 1]);

        foreach (float[] phase in Phases)
        {
            float sum = 0.0f;
            for (int tap = 0; tap < Taps; tap++)
            {
                sum += phase[tap] * window[tap];
            }

            peak = Math.Max(peak, Math.Abs(sum));
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
    /// The three fractional phases: the point p/4 of the way from window[5] to window[6], from
    /// window[0] to window[11]. A Hann window over plus and minus six and a half samples.
    /// </summary>
    private static float[][] Design()
    {
        var phases = new float[3][];
        for (int p = 1; p <= 3; p++)
        {
            double fraction = p / 4.0;
            var taps = new float[Taps];
            double sum = 0.0;
            for (int tap = 0; tap < Taps; tap++)
            {
                double t = (tap - (Delay - 1)) - fraction;
                double sinc = Math.Abs(t) < 1e-12 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
                double window = Math.Abs(t) >= 6.5 ? 0.0 : 0.5 * (1.0 + Math.Cos(Math.PI * t / 6.5));
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
}
