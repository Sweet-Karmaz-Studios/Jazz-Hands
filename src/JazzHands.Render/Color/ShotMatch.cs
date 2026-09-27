using System.Numerics;

namespace JazzHands.Render.Color;

/// <summary>
/// A Colour Wheels grade: each of lift, gamma and gain as red, green, blue and a master (w) added
/// to all three, then saturation. Offset, contrast and pivot are the effect's own and stay put.
/// </summary>
/// <param name="Lift">Shadows.</param>
/// <param name="Gamma">Mid tones.</param>
/// <param name="Gain">Highlights.</param>
/// <param name="Saturation">How colourful afterwards; 1 unchanged.</param>
/// <param name="Offset">Everything, added first; held.</param>
/// <param name="Contrast">About the pivot; held.</param>
/// <param name="Pivot">The tone contrast turns about; held.</param>
public sealed record WheelsGrade(
    Vector4 Lift,
    Vector4 Gamma,
    Vector4 Gain,
    float Saturation,
    Vector4 Offset = default,
    float Contrast = 1,
    float Pivot = 0.435f)
{
    /// <summary>The grade that changes nothing.</summary>
    public static WheelsGrade Neutral { get; } = new(Vector4.Zero, Vector4.Zero, Vector4.Zero, 1);

    /// <summary>The top of the domain the grade works in, where its result is held: 1 for sRGB, about 1.468 for ACEScct.</summary>
    public float Top { get; init; } = 1;

    /// <summary>
    /// An encoded colour through the grade, as <c>PsWheels</c> in Grading.hlsl has it, down to the
    /// order: offset, gain, lift, gamma, contrast, saturation, then held inside 0 to <see cref="Top"/>.
    /// </summary>
    public Vector3 Apply(Vector3 v)
    {
        var master = new Vector3(Offset.W);
        v += new Vector3(Offset.X, Offset.Y, Offset.Z) + master;
        v *= Vector3.One + Xyz(Gain) + new Vector3(Gain.W);
        v += (Xyz(Lift) + new Vector3(Lift.W)) * (Vector3.One - v);
        Vector3 power = Vector3.One + Xyz(Gamma) + new Vector3(Gamma.W);
        v = new Vector3(
            MathF.Pow(MathF.Max(v.X, 0), 1 / MathF.Max(power.X, 0.01f)),
            MathF.Pow(MathF.Max(v.Y, 0), 1 / MathF.Max(power.Y, 0.01f)),
            MathF.Pow(MathF.Max(v.Z, 0), 1 / MathF.Max(power.Z, 0.01f)));
        v = ((v - new Vector3(Pivot)) * Contrast) + new Vector3(Pivot);
        float luma = Vector3.Dot(v, new Vector3(0.2126f, 0.7152f, 0.0722f));
        v = Vector3.Lerp(new Vector3(luma), v, Saturation);
        return Vector3.Clamp(v, Vector3.Zero, new Vector3(Top));
    }

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);
}

/// <summary>
/// Shot matching (Phase 44): the Colour Wheels grade that makes one frame's colour look like
/// another's.
/// </summary>
/// <remarks>
/// <para>
/// Both frames are read as the wheels see them, sRGB-encoded, sampled evenly over the picture.
/// Each channel's tones are described by 41 quantiles, from the 1st to the 99th percentile, so
/// shadows, mid tones and highlights each have their say, and colourfulness by the quantiles of
/// Oklab chroma. The two frames need not be the same picture: this matches how the tones and the
/// colours are spread, which is what makes two shots cut together.
/// </para>
/// <para>
/// First each channel's lift, gamma and gain are fitted on its own to carry the frame's quantiles
/// onto the reference's, by least squares; then all nine and the saturation are refined together
/// against the whole grade, saturation's mixing of the channels included, running every sample
/// through <see cref="WheelsGrade.Apply"/>. Both are Nelder and Mead's simplex search, with a small
/// pull towards no change so a flat frame, which says nothing about some tones, is left alone
/// there. Each wheel is written back as its three colours about a master, their mean, so the
/// panel shows a brightness move and a colour move rather than three brightness moves.
/// </para>
/// </remarks>
public static class ShotMatch
{
    /// <summary>How many quantiles describe a channel.</summary>
    public const int Quantiles = 41;

    private const int Samples = 6000;
    private const double Pull = 1e-4;
    private const double ChromaWeight = 4;

    /// <summary>
    /// The grade that takes <paramref name="frame"/> to look like <paramref name="reference"/>,
    /// both premultiplied linear BT.709, four floats a pixel. Offset, contrast and pivot are
    /// taken from <paramref name="held"/> and kept.
    /// </summary>
    public static WheelsGrade Solve(ReadOnlySpan<float> frame, ReadOnlySpan<float> reference, WheelsGrade? held = null, GradingDomain? domain = null)
    {
        domain ??= GradingDomain.Srgb;
        WheelsGrade start = (held ?? WheelsGrade.Neutral) with { Top = domain.Top };
        Vector3[] samples = Sample(frame, domain);
        Vector3[] wanted = Sample(reference, domain);
        if (samples.Length == 0 || wanted.Length == 0)
        {
            return start;
        }

        // What the frame looks like with the held part of the grade (offset, contrast) applied,
        // one channel at a time, is what each channel's own fit starts from.
        WheelsGrade heldOnly = start with { Lift = Vector4.Zero, Gamma = Vector4.Zero, Gain = Vector4.Zero, Saturation = 1 };
        double[][] target = [.. Enumerable.Range(0, 3).Select(channel => QuantilesOf(wanted, channel))];
        double[] targetChroma = ChromaQuantiles(wanted, domain);
        double[][] source = [.. Enumerable.Range(0, 3).Select(channel => QuantilesOf(samples, channel))];

        var fit = new double[10];
        fit[9] = 1;
        for (int channel = 0; channel < 3; channel++)
        {
            int c = channel;
            double[] best = NelderMead.Minimize(
                p =>
                {
                    double error = 0;
                    for (int k = 0; k < Quantiles; k++)
                    {
                        Vector3 v = Channel(source[c][k], c);
                        float graded = Axis(Grade(heldOnly, [p[0], 0, 0, p[1], 0, 0, p[2], 0, 0, 1], c).Apply(v), c);
                        double difference = graded - target[c][k];
                        error += difference * difference;
                    }

                    return (error / Quantiles) + Penalty(p);
                },
                [0, 0, 0],
                0.05,
                600);
            fit[c] = best[0];
            fit[3 + c] = best[1];
            fit[6 + c] = best[2];
        }

        double[] joint = NelderMead.Minimize(
            p => Mismatch(Grade(start, p), samples, target, targetChroma, domain) + Penalty(p.AsSpan(0, 9)) + (Pull * (p[9] - 1) * (p[9] - 1)),
            fit,
            0.03,
            900);

        return Tidy(Grade(start, joint));
    }

    /// <summary>The frame's pixels, straight and encoded as the wheels see them, about <see cref="Samples"/> of them spread evenly.</summary>
    public static Vector3[] Sample(ReadOnlySpan<float> premultiplied, GradingDomain? domain = null)
    {
        domain ??= GradingDomain.Srgb;
        int pixels = premultiplied.Length / 4;
        if (pixels == 0)
        {
            return [];
        }

        int step = Math.Max(1, pixels / Samples);
        var samples = new List<Vector3>((pixels / step) + 1);
        for (int pixel = step / 2; pixel < pixels; pixel += step)
        {
            if (premultiplied[(pixel * 4) + 3] < 0.5f)
            {
                continue;
            }

            Vector3 linear = ColorDifference.Straight(premultiplied, pixel);
            samples.Add(Vector3.Clamp(
                new Vector3(domain.Encode(linear.X), domain.Encode(linear.Y), domain.Encode(linear.Z)),
                Vector3.Zero,
                new Vector3(domain.Top)));
        }

        return [.. samples];
    }

    private static double Mismatch(WheelsGrade grade, Vector3[] samples, double[][] target, double[] targetChroma, GradingDomain domain)
    {
        var graded = new Vector3[samples.Length];
        for (int index = 0; index < samples.Length; index++)
        {
            graded[index] = grade.Apply(samples[index]);
        }

        double error = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            double[] quantiles = QuantilesOf(graded, channel);
            for (int k = 0; k < Quantiles; k++)
            {
                double difference = quantiles[k] - target[channel][k];
                error += difference * difference;
            }
        }

        double[] chroma = ChromaQuantiles(graded, domain);
        for (int k = 0; k < Quantiles; k++)
        {
            double difference = chroma[k] - targetChroma[k];
            error += ChromaWeight * difference * difference;
        }

        return error / (4 * Quantiles);
    }

    private static double[] QuantilesOf(Vector3[] values, int channel)
    {
        float[] sorted = [.. values.Select(value => Axis(value, channel))];
        Array.Sort(sorted);
        return Quantile(sorted);
    }

    private static double[] ChromaQuantiles(Vector3[] encoded, GradingDomain domain)
    {
        float[] sorted = [.. encoded.Select(value => ColorDifference.Chroma(domain.ToRec709(new Vector3(
            domain.Decode(value.X), domain.Decode(value.Y), domain.Decode(value.Z)))))];
        Array.Sort(sorted);
        return Quantile(sorted);
    }

    private static double[] Quantile(float[] sorted)
    {
        var quantiles = new double[Quantiles];
        for (int k = 0; k < Quantiles; k++)
        {
            double position = (0.01 + (0.98 * k / (Quantiles - 1))) * (sorted.Length - 1);
            int below = (int)Math.Floor(position);
            int above = Math.Min(below + 1, sorted.Length - 1);
            double t = position - below;
            quantiles[k] = (sorted[below] * (1 - t)) + (sorted[above] * t);
        }

        return quantiles;
    }

    /// <summary>A grade from the search's ten numbers: lift, gamma and gain for red, green and blue, then saturation.</summary>
    private static WheelsGrade Grade(WheelsGrade held, double[] p, int? only = null)
    {
        float Value(int index, float low, float high) => (float)Math.Clamp(p[index], low, high);

        Vector4 Wheel(int first, float low, float high) => only is { } channel
            ? Pick(channel, Value(first, low, high))
            : new Vector4(Value(first, low, high), Value(first + 1, low, high), Value(first + 2, low, high), 0);

        return held with
        {
            Lift = Wheel(0, -1, 1),
            Gamma = Wheel(3, -0.9f, 3),
            Gain = Wheel(6, -0.9f, 3),
            Saturation = only is null ? Value(9, 0, 4) : 1,
        };
    }

    private static Vector4 Pick(int channel, float value) => channel switch
    {
        0 => new Vector4(value, 0, 0, 0),
        1 => new Vector4(0, value, 0, 0),
        _ => new Vector4(0, 0, value, 0),
    };

    private static Vector3 Channel(double value, int channel) => channel switch
    {
        0 => new Vector3((float)value, 0, 0),
        1 => new Vector3(0, (float)value, 0),
        _ => new Vector3(0, 0, (float)value),
    };

    private static float Axis(Vector3 value, int channel) => channel switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z,
    };

    private static double Penalty(ReadOnlySpan<double> p)
    {
        double sum = 0;
        foreach (double value in p)
        {
            sum += value * value;
        }

        return Pull * sum;
    }

    /// <summary>Each wheel as its colours about a master, rounded to what a person would type.</summary>
    private static WheelsGrade Tidy(WheelsGrade grade)
    {
        static Vector4 AboutMaster(Vector4 wheel)
        {
            float master = (wheel.X + wheel.Y + wheel.Z) / 3;
            return new Vector4(Round(wheel.X - master), Round(wheel.Y - master), Round(wheel.Z - master), Round(master));
        }

        return grade with
        {
            Lift = AboutMaster(grade.Lift),
            Gamma = AboutMaster(grade.Gamma),
            Gain = AboutMaster(grade.Gain),
            Saturation = Round(grade.Saturation),
        };
    }

    private static float Round(float value) => MathF.Round(value, 4);
}

/// <summary>Nelder and Mead's downhill simplex: a minimum without derivatives, for small problems.</summary>
internal static class NelderMead
{
    /// <summary>The point near <paramref name="start"/> where <paramref name="f"/> is least, within a budget of evaluations.</summary>
    public static double[] Minimize(Func<double[], double> f, double[] start, double step, int evaluations)
    {
        int n = start.Length;
        var points = new double[n + 1][];
        var values = new double[n + 1];
        for (int index = 0; index <= n; index++)
        {
            points[index] = (double[])start.Clone();
            if (index > 0)
            {
                points[index][index - 1] += step;
            }

            values[index] = f(points[index]);
        }

        int used = n + 1;
        while (used < evaluations)
        {
            Array.Sort(values, points);
            if (values[n] - values[0] < 1e-12)
            {
                break;
            }

            var centroid = new double[n];
            for (int index = 0; index < n; index++)
            {
                for (int d = 0; d < n; d++)
                {
                    centroid[d] += points[index][d] / n;
                }
            }

            double[] Toward(double scale) => [.. centroid.Select((c, d) => c + (scale * (points[n][d] - c)))];

            double[] reflected = Toward(-1);
            double reflectedValue = f(reflected);
            used++;
            if (reflectedValue < values[0])
            {
                double[] expanded = Toward(-2);
                double expandedValue = f(expanded);
                used++;
                (points[n], values[n]) = expandedValue < reflectedValue ? (expanded, expandedValue) : (reflected, reflectedValue);
            }
            else if (reflectedValue < values[n - 1])
            {
                (points[n], values[n]) = (reflected, reflectedValue);
            }
            else
            {
                double[] contracted = Toward(reflectedValue < values[n] ? -0.5 : 0.5);
                double contractedValue = f(contracted);
                used++;
                if (contractedValue < Math.Min(reflectedValue, values[n]))
                {
                    (points[n], values[n]) = (contracted, contractedValue);
                }
                else
                {
                    for (int index = 1; index <= n; index++)
                    {
                        points[index] = [.. points[index].Select((value, d) => points[0][d] + (0.5 * (value - points[0][d])))];
                        values[index] = f(points[index]);
                    }

                    used += n;
                }
            }
        }

        Array.Sort(values, points);
        return points[0];
    }
}

/// <summary>
/// What the Colour Wheels work on (Phase 44): sRGB-encoded linear BT.709 in a display-referred
/// project, ACEScct of ACEScg in an ACES one; with the way back, where the grade is held, and the
/// way to linear BT.709 for judging colourfulness.
/// </summary>
/// <param name="Encode">Linear light to what the wheels see.</param>
/// <param name="Decode">What the wheels see back to linear light.</param>
/// <param name="Top">Where the wheels' result is held.</param>
/// <param name="ToRec709">The domain's linear light as linear BT.709.</param>
public sealed record GradingDomain(Func<float, float> Encode, Func<float, float> Decode, float Top, Func<Vector3, Vector3> ToRec709)
{
    /// <summary>A display-referred project's: sRGB, up to 1.</summary>
    public static GradingDomain Srgb { get; } = new(ColorDifference.ToSrgb, ColorDifference.FromSrgb, 1, linear => linear);

    /// <summary>An ACES project's: ACEScct of ACEScg, up to its top.</summary>
    public static GradingDomain Acescct { get; } = new(
        linear => (float)Aces.AcesInput.ToAcescct(linear),
        cct => (float)Aces.AcesInput.FromAcescct(cct),
        1.4679964f,
        ap1 =>
        {
            Aces.D3 rec709 = new Aces.D3(ap1.X, ap1.Y, ap1.Z) * Ap1ToRec709;
            return new Vector3((float)rec709.X, (float)rec709.Y, (float)rec709.Z);
        });

    private static readonly Aces.M33 Ap1ToRec709 = Aces.Chromaticities.Ap1.To(Aces.Chromaticities.Rec709);
}
