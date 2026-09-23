using System.Diagnostics;
using System.Globalization;

namespace JazzHands.Core.Time;

/// <summary>
/// An exact rational number, always normalized with a positive denominator. Frame rates and
/// sample rates are Rational, never double: 30000/1001 is a frame rate, 29.97 is a rounding error.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public readonly record struct Rational : IComparable<Rational>
{
    /// <summary>Zero (0/1).</summary>
    public static readonly Rational Zero = new(0, 1);

    /// <summary>One (1/1).</summary>
    public static readonly Rational One = new(1, 1);

    /// <summary>23.976 fps (24000/1001).</summary>
    public static readonly Rational Fps23976 = new(24000, 1001);

    /// <summary>24 fps.</summary>
    public static readonly Rational Fps24 = new(24, 1);

    /// <summary>25 fps.</summary>
    public static readonly Rational Fps25 = new(25, 1);

    /// <summary>29.97 fps (30000/1001).</summary>
    public static readonly Rational Fps2997 = new(30000, 1001);

    /// <summary>30 fps.</summary>
    public static readonly Rational Fps30 = new(30, 1);

    /// <summary>48 fps.</summary>
    public static readonly Rational Fps48 = new(48, 1);

    /// <summary>50 fps.</summary>
    public static readonly Rational Fps50 = new(50, 1);

    /// <summary>59.94 fps (60000/1001).</summary>
    public static readonly Rational Fps5994 = new(60000, 1001);

    /// <summary>60 fps.</summary>
    public static readonly Rational Fps60 = new(60, 1);

    /// <summary>90 fps.</summary>
    public static readonly Rational Fps90 = new(90, 1);

    /// <summary>100 fps.</summary>
    public static readonly Rational Fps100 = new(100, 1);

    /// <summary>119.88 fps (120000/1001).</summary>
    public static readonly Rational Fps11988 = new(120000, 1001);

    /// <summary>120 fps.</summary>
    public static readonly Rational Fps120 = new(120, 1);

    /// <summary>The rates Jazz Hands offers in the UI and accepts for project settings.</summary>
    public static readonly IReadOnlyList<Rational> StandardFrameRates =
    [
        Fps23976, Fps24, Fps25, Fps2997, Fps30, Fps48,
        Fps50, Fps5994, Fps60, Fps90, Fps100, Fps11988, Fps120,
    ];

    /// <summary>Creates a normalized rational. The denominator must not be zero.</summary>
    public Rational(long num, long den)
    {
        if (den == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(den), "A rational cannot have a zero denominator.");
        }

        if (num == 0)
        {
            Num = 0;
            Den = 1;
            return;
        }

        if (den < 0)
        {
            // long.MinValue has no positive counterpart, so negating it would silently overflow.
            if (den == long.MinValue || num == long.MinValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(den),
                    "Rational components cannot be long.MinValue with a negative denominator.");
            }

            num = -num;
            den = -den;
        }

        long g = Gcd(Math.Abs(num), den);
        Num = num / g;
        Den = den / g;
    }

    /// <summary>Numerator. Carries the sign.</summary>
    public long Num { get; }

    /// <summary>Denominator. Always greater than zero.</summary>
    public long Den { get; }

    /// <summary>True when this is zero.</summary>
    public bool IsZero => Num == 0;

    /// <summary>The reciprocal. Throws when this is zero.</summary>
    public Rational Inverse => Num == 0
        ? throw new DivideByZeroException("Cannot invert a zero rational.")
        : new Rational(Den, Num);

    /// <summary>The nominal integer rate used by timecode: 30 for 30000/1001, 60 for 60000/1001.</summary>
    public int NominalRate => checked((int)((Num + Den - 1) / Den));

    /// <summary>True for rates whose denominator is 1001, which are the drop-frame candidates.</summary>
    public bool IsNtsc => Den == 1001;

    public static Rational operator +(Rational a, Rational b) =>
        FromBig(((Int128)a.Num * b.Den) + ((Int128)b.Num * a.Den), (Int128)a.Den * b.Den);

    public static Rational operator -(Rational a, Rational b) =>
        FromBig(((Int128)a.Num * b.Den) - ((Int128)b.Num * a.Den), (Int128)a.Den * b.Den);

    public static Rational operator -(Rational a) => new(-a.Num, a.Den);

    public static Rational operator *(Rational a, Rational b) =>
        FromBig((Int128)a.Num * b.Num, (Int128)a.Den * b.Den);

    public static Rational operator *(Rational a, long b) => FromBig((Int128)a.Num * b, a.Den);

    public static Rational operator /(Rational a, Rational b) => b.IsZero
        ? throw new DivideByZeroException("Cannot divide a rational by zero.")
        : FromBig((Int128)a.Num * b.Den, (Int128)a.Den * b.Num);

    public static Rational operator /(Rational a, long b) => b == 0
        ? throw new DivideByZeroException("Cannot divide a rational by zero.")
        : FromBig(a.Num, (Int128)a.Den * b);

    public static bool operator <(Rational a, Rational b) => a.CompareTo(b) < 0;

    public static bool operator <=(Rational a, Rational b) => a.CompareTo(b) <= 0;

    public static bool operator >(Rational a, Rational b) => a.CompareTo(b) > 0;

    public static bool operator >=(Rational a, Rational b) => a.CompareTo(b) >= 0;

    /// <summary>Named alternate for the addition operator.</summary>
    public static Rational Add(Rational a, Rational b) => a + b;

    /// <summary>Named alternate for the subtraction operator.</summary>
    public static Rational Subtract(Rational a, Rational b) => a - b;

    /// <summary>Named alternate for the multiplication operator.</summary>
    public static Rational Multiply(Rational a, Rational b) => a * b;

    /// <summary>Named alternate for the division operator.</summary>
    public static Rational Divide(Rational a, Rational b) => a / b;

    /// <summary>Named alternate for the unary negation operator.</summary>
    public static Rational Negate(Rational a) => -a;

    /// <summary>
    /// Parses "30", "30000/1001", or a decimal such as "29.97". Decimals within a thousandth of a
    /// standard NTSC rate snap to the exact 1001 form, because someone typing 29.97 means 30000/1001.
    /// Any other decimal becomes its exact rational expansion.
    /// </summary>
    public static Rational Parse(string text) => TryParse(text, out Rational value)
        ? value
        : throw new FormatException($"'{text}' is not a frame rate. Use 30, 30000/1001, or 29.97.");

    /// <summary>Parses a frame rate. See <see cref="Parse"/> for the accepted forms.</summary>
    public static bool TryParse(string? text, out Rational value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> s = text.AsSpan().Trim();

        int slash = s.IndexOf('/');
        if (slash >= 0)
        {
            if (!long.TryParse(s[..slash].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ||
                !long.TryParse(s[(slash + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long d) ||
                d == 0)
            {
                return false;
            }

            value = new Rational(n, d);
            return true;
        }

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
        {
            value = new Rational(whole, 1);
            return true;
        }

        if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal dec))
        {
            return false;
        }

        foreach (Rational standard in StandardFrameRates)
        {
            if (standard.IsNtsc && Math.Abs(((decimal)standard.Num / standard.Den) - dec) < 0.005m)
            {
                value = standard;
                return true;
            }
        }

        long den = 1;
        while (dec != decimal.Truncate(dec) && den <= 1_000_000_000L)
        {
            dec *= 10;
            den *= 10;
        }

        if (dec != decimal.Truncate(dec))
        {
            return false;
        }

        value = new Rational((long)dec, den);
        return true;
    }

    /// <summary>
    /// The value as a double. For display, logging and FFmpeg filter strings only.
    /// Never compare frame rates through this.
    /// </summary>
    public double ToDouble() => (double)Num / Den;

    /// <inheritdoc />
    public int CompareTo(Rational other) => ((Int128)Num * other.Den).CompareTo((Int128)other.Num * Den);

    /// <summary>Renders as "30" for integers and "30000/1001" otherwise. This is the wire and CLI form.</summary>
    public override string ToString() => Den == 1
        ? Num.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Num}/{Den}");

    /// <summary>
    /// Renders for a person to read: "30", "29.97", "23.976".
    /// </summary>
    /// <remarks>
    /// For labels and columns only. It is lossy and is never parsed back; <see cref="ToString"/>
    /// is the form the project file, the wire and the CLI use.
    /// </remarks>
    public string ToDisplayString() => Den == 1
        ? Num.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{ToDouble():0.###}");

    private static Rational FromBig(Int128 num, Int128 den)
    {
        if (den == 0)
        {
            throw new DivideByZeroException("A rational cannot have a zero denominator.");
        }

        if (den < 0)
        {
            num = -num;
            den = -den;
        }

        Int128 g = BigGcd(num < 0 ? -num : num, den);
        if (g > 1)
        {
            num /= g;
            den /= g;
        }

        if (num < long.MinValue || num > long.MaxValue || den > long.MaxValue)
        {
            throw new OverflowException("The rational result does not fit in 64-bit components.");
        }

        return new Rational((long)num, (long)den);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }

    private static Int128 BigGcd(Int128 a, Int128 b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? Int128.One : a;
    }
}
