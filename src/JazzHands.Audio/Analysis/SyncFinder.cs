namespace JazzHands.Audio.Analysis;

/// <summary>How two recordings of the same sound line up.</summary>
/// <param name="LagSeconds">How much later the first has the sound than the second: move the first earlier by this to line them up.</param>
/// <param name="Confidence">How alike their loudness is over time once lined up, 0 to 1: over 0.5 is a sure match.</param>
/// <param name="Ambiguous">True when another lag matched nearly as well, as a steady rhythm does.</param>
public sealed record SyncMatch(double LagSeconds, double Confidence, bool Ambiguous);

/// <summary>
/// Finds the offset between two recordings of the same moment, such as a camera's sound and a
/// separate microphone: to a fraction of a millisecond.
/// </summary>
/// <remarks>
/// <para>
/// Two passes. The coarse one compares how the loudness of each rises and falls, in decibels
/// every 5 ms, which two microphones in different places share even when their tone does not:
/// the rises (onsets) of both are cross-correlated over their whole length through an FFT, and
/// the best lag found. The fine one takes the loudest second or so of the second recording, and
/// the first around the coarse lag, and cross-correlates the samples themselves, placing the peak
/// between samples with a parabola.
/// </para>
/// <para>
/// Confidence is the correlation of the two loudness curves where they overlap once lined up. A
/// second lag scoring nearly as well (a click track, a steady beat) makes it ambiguous.
/// </para>
/// </remarks>
public static class SyncFinder
{
    /// <summary>Loudness readings a second for the coarse pass.</summary>
    public const int EnvelopeRate = 200;

    /// <summary>The longest stretch of samples the fine pass compares.</summary>
    private const int FineLength = 1 << 16;

    /// <summary>Finds how much later <paramref name="first"/> has the sound than <paramref name="second"/>.</summary>
    /// <param name="first">The recording to move, mono.</param>
    /// <param name="second">The recording to line it up with, mono, at the same rate.</param>
    /// <param name="rate">Their sample rate.</param>
    /// <returns>The match, or null when either is silent or too short to say.</returns>
    public static SyncMatch? Find(ReadOnlySpan<float> first, ReadOnlySpan<float> second, int rate)
    {
        int hop = Math.Max(1, rate / EnvelopeRate);
        double[] loudA = Loudness(first, hop);
        double[] loudB = Loudness(second, hop);
        if (loudA.Length < 20 || loudB.Length < 20)
        {
            return null;
        }

        double[] onsetA = Normalised(Onsets(loudA));
        double[] onsetB = Normalised(Onsets(loudB));
        if (onsetA.Length == 0 || onsetB.Length == 0)
        {
            return null;
        }

        // Coarse: the lag in loudness readings with the strongest correlation, and the best
        // elsewhere, a tenth of a second or more away.
        double[] correlation = Correlate(onsetA, onsetB, out int zero);
        int best = Array.IndexOf(correlation, correlation.Max());
        int guard = EnvelopeRate / 10;
        double runnerUp = correlation.Where((_, index) => Math.Abs(index - best) > guard).DefaultIfEmpty(0).Max();
        int lag = best - zero;

        double confidence = Math.Clamp(Pearson(loudA, loudB, lag), 0, 1);
        double fine = Refine(first, second, lag * hop, 3 * hop);
        return new SyncMatch(fine / rate, Math.Round(confidence, 3), runnerUp > correlation[best] * 0.8);
    }

    /// <summary>Loudness in decibels over each hop.</summary>
    private static double[] Loudness(ReadOnlySpan<float> samples, int hop)
    {
        double[] loud = new double[samples.Length / hop];
        for (int index = 0; index < loud.Length; index++)
        {
            double sum = 0;
            foreach (float sample in samples.Slice(index * hop, hop))
            {
                sum += (double)sample * sample;
            }

            loud[index] = 10.0 * Math.Log10(1e-10 + (sum / hop));
        }

        return loud;
    }

    /// <summary>How much the loudness rose into each reading; falls count as nothing.</summary>
    private static double[] Onsets(double[] loud)
    {
        double[] rises = new double[loud.Length];
        for (int index = 1; index < loud.Length; index++)
        {
            rises[index] = Math.Max(0, loud[index] - loud[index - 1]);
        }

        return rises;
    }

    /// <summary>With its mean taken off and scaled to a standard deviation of one; empty when it never changes.</summary>
    private static double[] Normalised(double[] values)
    {
        double mean = values.Average();
        double deviation = Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / values.Length);
        return deviation < 1e-9 ? [] : [.. values.Select(value => (value - mean) / deviation)];
    }

    /// <summary>
    /// The cross-correlation of two series at every lag, through an FFT: entry <c>zero + k</c> is
    /// the sum of a[n + k] times b[n].
    /// </summary>
    private static double[] Correlate(double[] a, double[] b, out int zero)
    {
        int size = Fft.SizeFor(a.Length + b.Length);
        var fft = new Fft(size);
        double[] ar = new double[size], ai = new double[size], br = new double[size], bi = new double[size];
        a.CopyTo(ar, 0);
        b.CopyTo(br, 0);
        fft.Forward(ar, ai);
        fft.Forward(br, bi);
        for (int index = 0; index < size; index++)
        {
            // a times the conjugate of b.
            double re = (ar[index] * br[index]) + (ai[index] * bi[index]);
            double im = (ai[index] * br[index]) - (ar[index] * bi[index]);
            ar[index] = re;
            ai[index] = im;
        }

        fft.Inverse(ar, ai);

        // Lags from -(b - 1) to a - 1: the negative ones wrap to the end.
        zero = b.Length - 1;
        double[] lags = new double[a.Length + b.Length - 1];
        for (int lag = -(b.Length - 1); lag < a.Length; lag++)
        {
            lags[lag + zero] = ar[lag < 0 ? size + lag : lag];
        }

        return lags;
    }

    /// <summary>The correlation of a and b where they overlap with a moved <paramref name="lag"/> readings later.</summary>
    private static double Pearson(double[] a, double[] b, int lag)
    {
        int from = Math.Max(0, -lag);
        int to = Math.Min(b.Length, a.Length - lag);
        if (to - from < 10)
        {
            return 0;
        }

        double meanA = 0, meanB = 0;
        for (int index = from; index < to; index++)
        {
            meanA += a[index + lag];
            meanB += b[index];
        }

        meanA /= to - from;
        meanB /= to - from;
        double covariance = 0, varianceA = 0, varianceB = 0;
        for (int index = from; index < to; index++)
        {
            double x = a[index + lag] - meanA;
            double y = b[index] - meanB;
            covariance += x * y;
            varianceA += x * x;
            varianceB += y * y;
        }

        return varianceA <= 0 || varianceB <= 0 ? 0 : covariance / Math.Sqrt(varianceA * varianceB);
    }

    /// <summary>
    /// The lag in samples to a fraction: the loudest stretch of the second recording against the
    /// first around the coarse lag, correlated sample by sample, the peak placed by a parabola.
    /// The coarse lag when the recordings do not overlap enough to compare samples.
    /// </summary>
    private static double Refine(ReadOnlySpan<float> first, ReadOnlySpan<float> second, int coarse, int margin)
    {
        // Where the second recording can be compared: the first must hold it, and the margin
        // either side, once moved by the coarse lag.
        int from = Math.Max(0, margin - coarse);
        int to = Math.Min(second.Length, first.Length - coarse - margin);
        int length = Math.Min(FineLength, to - from);
        if (length < 1024)
        {
            return coarse;
        }

        // The loudest stretch of that, in steps of a quarter of its length.
        int start = from;
        double loudest = -1;
        for (int at = from; at + length <= to; at += Math.Max(1, length / 4))
        {
            double energy = 0;
            foreach (float sample in second.Slice(at, length))
            {
                energy += (double)sample * sample;
            }

            if (energy > loudest)
            {
                (loudest, start) = (energy, at);
            }
        }

        double[] b = new double[length];
        double[] a = new double[length + (2 * margin)];
        for (int index = 0; index < b.Length; index++)
        {
            b[index] = second[start + index];
        }

        for (int index = 0; index < a.Length; index++)
        {
            a[index] = first[start + coarse - margin + index];
        }

        // Entry zero + j is a[n + j] times b[n]: j = margin is the coarse lag itself.
        double[] correlation = Correlate(a, b, out int zero);
        int best = zero;
        for (int j = zero; j <= zero + (2 * margin); j++)
        {
            if (correlation[j] > correlation[best])
            {
                best = j;
            }
        }

        double offset = 0;
        if (best > zero && best < zero + (2 * margin))
        {
            double left = correlation[best - 1], middle = correlation[best], right = correlation[best + 1];
            double curve = left - (2 * middle) + right;
            offset = curve < 0 ? 0.5 * (left - right) / curve : 0;
        }

        return coarse - margin + (best - zero) + offset;
    }
}
