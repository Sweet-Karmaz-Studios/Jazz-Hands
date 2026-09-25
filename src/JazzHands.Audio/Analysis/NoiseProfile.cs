using System.Globalization;

namespace JazzHands.Audio.Analysis;

/// <summary>
/// The spectrum of a recording's background noise: its power in sixth-octave bands from 20 Hz,
/// learned from a stretch with nothing but the noise in it, for noise reduction to take out.
/// </summary>
/// <remarks>
/// Kept as text on the <c>audio.denoise</c> effect (one level in dB per band, comma separated), so
/// the project file stays readable and a profile learned at one rate works at another: the effect
/// turns the bands into its FFT's bins when the mix is built. Levels are the power a bin of a
/// <see cref="FrameSize"/> point transform through <see cref="Window"/> reads, averaged over the
/// bins of the band.
/// </remarks>
public static class NoiseProfile
{
    /// <summary>The transform's size, which the effect uses too.</summary>
    public const int FrameSize = 1024;

    /// <summary>How many bands: sixth octaves from 20 Hz to about 20 kHz.</summary>
    public const int Bands = 61;

    /// <summary>The analysis and synthesis window: the square root of a periodic Hann, so the two together are a Hann.</summary>
    public static IReadOnlyList<double> Window { get; } = [.. Enumerable.Range(0, FrameSize).Select(index => Math.Sqrt(0.5 - (0.5 * Math.Cos(2 * Math.PI * index / FrameSize))))];

    /// <summary>A band's centre frequency.</summary>
    public static double Centre(int band) => 20.0 * Math.Pow(2, band / 6.0);

    /// <summary>
    /// Learns the noise of a stretch of mono samples: the average power per bin over frames half
    /// overlapped, averaged into bands. Null when the stretch is shorter than one frame.
    /// </summary>
    public static double[]? Learn(ReadOnlySpan<float> samples, int rate)
    {
        if (samples.Length < FrameSize)
        {
            return null;
        }

        var fft = new Fft(FrameSize);
        double[] real = new double[FrameSize], imaginary = new double[FrameSize];
        double[] power = new double[(FrameSize / 2) + 1];
        int frames = 0;
        for (int start = 0; start + FrameSize <= samples.Length; start += FrameSize / 2, frames++)
        {
            for (int index = 0; index < FrameSize; index++)
            {
                real[index] = samples[start + index] * Window[index];
                imaginary[index] = 0;
            }

            fft.Forward(real, imaginary);
            for (int bin = 0; bin < power.Length; bin++)
            {
                power[bin] += (real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin]);
            }
        }

        double[] bands = new double[Bands];
        int[] counts = new int[Bands];
        for (int bin = 1; bin < power.Length; bin++)
        {
            double frequency = (double)bin * rate / FrameSize;
            int band = (int)Math.Round(6 * Math.Log2(frequency / 20.0));
            if (band >= 0 && band < Bands)
            {
                bands[band] += power[bin] / frames;
                counts[band]++;
            }
        }

        // Bands narrower than a bin at the bottom have none of their own: they take their
        // neighbours' levels, as the effect would read them anyway.
        double?[] levels = [.. Enumerable.Range(0, Bands).Select(band => counts[band] > 0 ? (double?)Decibels(bands[band] / counts[band]) : null)];
        return Fill(levels);
    }

    /// <summary>The profile as the effect's text: a level in dB per band, comma separated.</summary>
    public static string Format(IReadOnlyList<double> bands) =>
        string.Join(",", bands.Select(level => Math.Round(level, 1).ToString("0.0", CultureInfo.InvariantCulture)));

    /// <summary>A profile read back from text, or null when the text is not one.</summary>
    public static double[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != Bands)
        {
            return null;
        }

        double[] bands = new double[Bands];
        for (int band = 0; band < Bands; band++)
        {
            if (!double.TryParse(parts[band], NumberStyles.Float, CultureInfo.InvariantCulture, out bands[band]) || !double.IsFinite(bands[band]))
            {
                return null;
            }
        }

        return bands;
    }

    /// <summary>The noise power each bin of a <see cref="FrameSize"/> point transform at a rate reads, from a profile's bands.</summary>
    public static double[] Bins(IReadOnlyList<double> bands, int rate)
    {
        double[] bins = new double[(FrameSize / 2) + 1];
        for (int bin = 0; bin < bins.Length; bin++)
        {
            double frequency = Math.Max(1.0, (double)bin * rate / FrameSize);
            double position = Math.Clamp(6 * Math.Log2(frequency / 20.0), 0, Bands - 1);
            int below = (int)Math.Floor(position);
            int above = Math.Min(below + 1, Bands - 1);
            double level = bands[below] + ((bands[above] - bands[below]) * (position - below));
            bins[bin] = Math.Pow(10, level / 10.0);
        }

        return bins;
    }

    private static double Decibels(double power) => 10.0 * Math.Log10(Math.Max(power, 1e-20));

    /// <summary>Gaps filled from the nearest bands with a level, in between by a straight line.</summary>
    private static double[] Fill(double?[] levels)
    {
        int[] known = [.. Enumerable.Range(0, levels.Length).Where(band => levels[band] is not null)];
        if (known.Length == 0)
        {
            return new double[levels.Length];
        }

        double[] filled = new double[levels.Length];
        for (int band = 0; band < levels.Length; band++)
        {
            int after = Array.FindIndex(known, candidate => candidate >= band);
            if (after < 0)
            {
                filled[band] = levels[known[^1]]!.Value;
            }
            else if (after == 0 || known[after] == band)
            {
                filled[band] = levels[known[after]]!.Value;
            }
            else
            {
                int left = known[after - 1], right = known[after];
                double t = (double)(band - left) / (right - left);
                filled[band] = levels[left]!.Value + ((levels[right]!.Value - levels[left]!.Value) * t);
            }
        }

        return filled;
    }
}
