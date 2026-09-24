namespace JazzHands.Audio;

/// <summary>
/// A second order filter section, the building block of the EQ, the de-esser's split and the
/// loudness meter's K weighting.
/// </summary>
/// <remarks>
/// Coefficients are the RBJ audio EQ cookbook's, normalised so a0 is one. The structure is
/// transposed direct form II with its two state values in double precision, which keeps a low
/// shelf at 30 Hz at 48 kHz, where the poles sit very close to the unit circle, from drifting
/// or hissing the way single precision state would. A struct so a chain of them is one array
/// with no objects behind it; mutate it through a <c>ref</c>, never a copy.
/// </remarks>
public struct Biquad
{
    private double _z1;
    private double _z2;

    /// <summary>The feedforward coefficient of the input.</summary>
    public double B0 { get; set; }

    /// <summary>The feedforward coefficient one sample back.</summary>
    public double B1 { get; set; }

    /// <summary>The feedforward coefficient two samples back.</summary>
    public double B2 { get; set; }

    /// <summary>The feedback coefficient one sample back, a0 divided out.</summary>
    public double A1 { get; set; }

    /// <summary>The feedback coefficient two samples back.</summary>
    public double A2 { get; set; }

    /// <summary>A filter that passes everything unchanged.</summary>
    public static Biquad Identity => new() { B0 = 1.0 };

    /// <summary>True when this passes everything unchanged.</summary>
    public readonly bool IsIdentity => B0 == 1.0 && B1 == 0.0 && B2 == 0.0 && A1 == 0.0 && A2 == 0.0;

    /// <summary>Filters one sample.</summary>
    public float Process(float input)
    {
        double y = (B0 * input) + _z1;
        _z1 = (B1 * input) - (A1 * y) + _z2;
        _z2 = (B2 * input) - (A2 * y);
        return (float)y;
    }

    /// <summary>Forgets the signal so far.</summary>
    public void Reset()
    {
        _z1 = 0.0;
        _z2 = 0.0;
    }

    /// <summary>Takes another filter's coefficients and keeps this one's state, so a change of setting does not click.</summary>
    public void Take(in Biquad coefficients)
    {
        B0 = coefficients.B0;
        B1 = coefficients.B1;
        B2 = coefficients.B2;
        A1 = coefficients.A1;
        A2 = coefficients.A2;
    }

    /// <summary>The gain at a frequency, in decibels.</summary>
    public readonly double MagnitudeDb(double frequency, double sampleRate)
    {
        double w = 2.0 * Math.PI * frequency / sampleRate;
        var z1 = new System.Numerics.Complex(Math.Cos(-w), Math.Sin(-w));
        System.Numerics.Complex z2 = z1 * z1;
        System.Numerics.Complex h = (B0 + (B1 * z1) + (B2 * z2)) / (1.0 + (A1 * z1) + (A2 * z2));
        return 20.0 * Math.Log10(Math.Max(h.Magnitude, 1e-12));
    }

    /// <summary>A bell: gain around a frequency, as wide as its Q says.</summary>
    public static Biquad Peak(double sampleRate, double frequency, double gainDb, double q)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        (double cos, double alpha) = Angle(sampleRate, frequency, q);
        return Normalise(1.0 + (alpha * a), -2.0 * cos, 1.0 - (alpha * a), 1.0 + (alpha / a), -2.0 * cos, 1.0 - (alpha / a));
    }

    /// <summary>A low shelf: gain below a frequency, with the cookbook's slope of one.</summary>
    public static Biquad LowShelf(double sampleRate, double frequency, double gainDb)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        (double cos, double alpha) = Angle(sampleRate, frequency, Math.Sqrt(0.5));
        double root = 2.0 * Math.Sqrt(a) * alpha;
        return Normalise(
            a * ((a + 1.0) - ((a - 1.0) * cos) + root),
            2.0 * a * ((a - 1.0) - ((a + 1.0) * cos)),
            a * ((a + 1.0) - ((a - 1.0) * cos) - root),
            (a + 1.0) + ((a - 1.0) * cos) + root,
            -2.0 * ((a - 1.0) + ((a + 1.0) * cos)),
            (a + 1.0) + ((a - 1.0) * cos) - root);
    }

    /// <summary>A high shelf: gain above a frequency.</summary>
    public static Biquad HighShelf(double sampleRate, double frequency, double gainDb)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        (double cos, double alpha) = Angle(sampleRate, frequency, Math.Sqrt(0.5));
        double root = 2.0 * Math.Sqrt(a) * alpha;
        return Normalise(
            a * ((a + 1.0) + ((a - 1.0) * cos) + root),
            -2.0 * a * ((a - 1.0) + ((a + 1.0) * cos)),
            a * ((a + 1.0) + ((a - 1.0) * cos) - root),
            (a + 1.0) - ((a - 1.0) * cos) + root,
            2.0 * ((a - 1.0) - ((a + 1.0) * cos)),
            (a + 1.0) - ((a - 1.0) * cos) - root);
    }

    /// <summary>A low pass, 12 dB an octave.</summary>
    public static Biquad LowPass(double sampleRate, double frequency, double q = 0.7071067811865476)
    {
        (double cos, double alpha) = Angle(sampleRate, frequency, q);
        return Normalise((1.0 - cos) / 2.0, 1.0 - cos, (1.0 - cos) / 2.0, 1.0 + alpha, -2.0 * cos, 1.0 - alpha);
    }

    /// <summary>A high pass, 12 dB an octave.</summary>
    public static Biquad HighPass(double sampleRate, double frequency, double q = 0.7071067811865476)
    {
        (double cos, double alpha) = Angle(sampleRate, frequency, q);
        return Normalise((1.0 + cos) / 2.0, -(1.0 + cos), (1.0 + cos) / 2.0, 1.0 + alpha, -2.0 * cos, 1.0 - alpha);
    }

    /// <summary>A band pass whose peak is at 0 dB.</summary>
    public static Biquad BandPass(double sampleRate, double frequency, double q)
    {
        (double cos, double alpha) = Angle(sampleRate, frequency, q);
        return Normalise(alpha, 0.0, -alpha, 1.0 + alpha, -2.0 * cos, 1.0 - alpha);
    }

    /// <summary>
    /// The two stages of ITU-R BS.1770's K weighting at a sample rate: a high shelf of about
    /// +4 dB above 1.5 kHz for the head, then a high pass at 38 Hz. Designed from the analogue
    /// prototype by the bilinear transform, which at 48 kHz gives the published coefficients.
    /// </summary>
    public static (Biquad Shelf, Biquad HighPass) KWeighting(double sampleRate)
    {
        double k = Math.Tan(Math.PI * 1681.974450955533 / sampleRate);
        const double Q1 = 0.7071752369554196;
        double vh = Math.Pow(10.0, 3.999843853973347 / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + (k / Q1) + (k * k);
        var shelf = new Biquad
        {
            B0 = (vh + (vb * k / Q1) + (k * k)) / a0,
            B1 = 2.0 * ((k * k) - vh) / a0,
            B2 = (vh - (vb * k / Q1) + (k * k)) / a0,
            A1 = 2.0 * ((k * k) - 1.0) / a0,
            A2 = (1.0 - (k / Q1) + (k * k)) / a0,
        };

        k = Math.Tan(Math.PI * 38.13547087602444 / sampleRate);
        const double Q2 = 0.5003270373238773;
        a0 = 1.0 + (k / Q2) + (k * k);
        var highPass = new Biquad
        {
            B0 = 1.0,
            B1 = -2.0,
            B2 = 1.0,
            A1 = 2.0 * ((k * k) - 1.0) / a0,
            A2 = (1.0 - (k / Q2) + (k * k)) / a0,
        };

        return (shelf, highPass);
    }

    private static (double Cos, double Alpha) Angle(double sampleRate, double frequency, double q)
    {
        double w0 = 2.0 * Math.PI * Math.Clamp(frequency, 1.0, (sampleRate / 2.0) * 0.99) / sampleRate;
        return (Math.Cos(w0), Math.Sin(w0) / (2.0 * Math.Max(q, 0.01)));
    }

    private static Biquad Normalise(double b0, double b1, double b2, double a0, double a1, double a2) =>
        new() { B0 = b0 / a0, B1 = b1 / a0, B2 = b2 / a0, A1 = a1 / a0, A2 = a2 / a0 };
}
