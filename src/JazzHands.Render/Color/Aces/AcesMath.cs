namespace JazzHands.Render.Color.Aces;

/// <summary>Three doubles: a colour, a JMh triple, or a row of a matrix.</summary>
/// <param name="X">The first.</param>
/// <param name="Y">The second.</param>
/// <param name="Z">The third.</param>
public readonly record struct D3(double X, double Y, double Z)
{
    /// <summary>All three the same.</summary>
    public static D3 All(double value) => new(value, value, value);

    /// <summary>One by index, 0 to 2.</summary>
    public double this[int index] => index switch
    {
        0 => X,
        1 => Y,
        _ => Z,
    };

    /// <summary>Each scaled.</summary>
    public static D3 operator *(double scale, D3 v) => new(scale * v.X, scale * v.Y, scale * v.Z);

    /// <summary>Added.</summary>
    public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Subtracted.</summary>
    public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>The largest of the three.</summary>
    public double Max => Math.Max(X, Math.Max(Y, Z));

    /// <summary>Each held inside a range.</summary>
    public D3 Clamp(double low, double high) => new(Math.Max(low, Math.Min(X, high)), Math.Max(low, Math.Min(Y, high)), Math.Max(low, Math.Min(Z, high)));

    /// <summary>A function of each.</summary>
    public D3 Map(Func<double, double> f) => new(f(X), f(Y), f(Z));

    /// <summary>Linear interpolation from one to another.</summary>
    public static D3 Lerp(D3 a, D3 b, double t) => a + (t * (b - a));
}

/// <summary>
/// A 3x3 matrix used the way the ACES CTL uses one: a colour is a row vector multiplied on the
/// left (<c>mult_f3_f33(v, M)</c> is <c>v * M</c>), and <c>mult_f33_f33(A, B)</c> is <c>A * B</c>, so
/// <c>v * (A * B)</c> applies A first.
/// </summary>
public readonly record struct M33(D3 Row0, D3 Row1, D3 Row2)
{
    /// <summary>The identity.</summary>
    public static M33 Identity { get; } = new(new D3(1, 0, 0), new D3(0, 1, 0), new D3(0, 0, 1));

    /// <summary>An element, by row and column.</summary>
    public double this[int row, int column] => (row switch
    {
        0 => Row0,
        1 => Row1,
        _ => Row2,
    })[column];

    /// <summary>A row vector times the matrix: the CTL's <c>mult_f3_f33</c>.</summary>
    public static D3 operator *(D3 v, M33 m) => new(
        (v.X * m.Row0.X) + (v.Y * m.Row1.X) + (v.Z * m.Row2.X),
        (v.X * m.Row0.Y) + (v.Y * m.Row1.Y) + (v.Z * m.Row2.Y),
        (v.X * m.Row0.Z) + (v.Y * m.Row1.Z) + (v.Z * m.Row2.Z));

    /// <summary>The product A * B: the CTL's <c>mult_f33_f33</c>.</summary>
    public static M33 operator *(M33 a, M33 b) => new(a.Row0 * b, a.Row1 * b, a.Row2 * b);

    /// <summary>Every element scaled.</summary>
    public static M33 operator *(double scale, M33 m) => new(scale * m.Row0, scale * m.Row1, scale * m.Row2);

    /// <summary>A diagonal matrix.</summary>
    public static M33 Diagonal(D3 d) => new(new D3(d.X, 0, 0), new D3(0, d.Y, 0), new D3(0, 0, d.Z));

    /// <summary>The inverse.</summary>
    public M33 Invert()
    {
        double a = this[0, 0], b = this[0, 1], c = this[0, 2];
        double d = this[1, 0], e = this[1, 1], f = this[1, 2];
        double g = this[2, 0], h = this[2, 1], i = this[2, 2];
        double det = (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
        double s = 1.0 / det;
        return new M33(
            new D3(s * ((e * i) - (f * h)), s * ((c * h) - (b * i)), s * ((b * f) - (c * e))),
            new D3(s * ((f * g) - (d * i)), s * ((a * i) - (c * g)), s * ((c * d) - (a * f))),
            new D3(s * ((d * h) - (e * g)), s * ((b * g) - (a * h)), s * ((a * e) - (b * d))));
    }

    /// <summary>The elements row by row, as floats, for a shader.</summary>
    public float[] ToFloats() =>
    [
        (float)Row0.X, (float)Row0.Y, (float)Row0.Z,
        (float)Row1.X, (float)Row1.Y, (float)Row1.Z,
        (float)Row2.X, (float)Row2.Y, (float)Row2.Z,
    ];
}

/// <summary>An RGB space's primaries and white, as CIE xy.</summary>
/// <param name="Red">The red primary.</param>
/// <param name="Green">The green primary.</param>
/// <param name="Blue">The blue primary.</param>
/// <param name="White">The white point.</param>
public readonly record struct Chromaticities((double X, double Y) Red, (double X, double Y) Green, (double X, double Y) Blue, (double X, double Y) White)
{
    /// <summary>ACES AP0, the primaries of ACES2065-1.</summary>
    public static Chromaticities Ap0 { get; } = new((0.73470, 0.26530), (0.00000, 1.00000), (0.00010, -0.07700), (0.32168, 0.33767));

    /// <summary>ACES AP1, the primaries of ACEScg and ACEScct.</summary>
    public static Chromaticities Ap1 { get; } = new((0.713, 0.293), (0.165, 0.830), (0.128, 0.044), (0.32168, 0.33767));

    /// <summary>BT.709 and sRGB, D65.</summary>
    public static Chromaticities Rec709 { get; } = new((0.6400, 0.3300), (0.3000, 0.6000), (0.1500, 0.0600), (0.3127, 0.3290));

    /// <summary>Display P3, D65.</summary>
    public static Chromaticities P3D65 { get; } = new((0.6800, 0.3200), (0.2650, 0.6900), (0.1500, 0.0600), (0.3127, 0.3290));

    /// <summary>BT.2020 and BT.2100, D65.</summary>
    public static Chromaticities Rec2020 { get; } = new((0.7080, 0.2920), (0.1700, 0.7970), (0.1310, 0.0460), (0.3127, 0.3290));

    /// <summary>Sony S-Gamut3, D65.</summary>
    public static Chromaticities SGamut3 { get; } = new((0.730, 0.280), (0.140, 0.855), (0.100, -0.050), (0.3127, 0.3290));

    /// <summary>ARRI Wide Gamut 3, D65.</summary>
    public static Chromaticities ArriWideGamut3 { get; } = new((0.6840, 0.3130), (0.2210, 0.8480), (0.0861, -0.1020), (0.3127, 0.3290));

    /// <summary>Panasonic V-Gamut, D65.</summary>
    public static Chromaticities VGamut { get; } = new((0.730, 0.280), (0.165, 0.840), (0.100, -0.030), (0.3127, 0.3290));

    /// <summary>RGB to XYZ as a row-vector matrix, for a white of luminance Y: the CTL's <c>RGBtoXYZ_f33</c>.</summary>
    public M33 RgbToXyz(double luminance = 1.0)
    {
        double x = White.X * luminance / White.Y;
        double z = (1.0 - White.X - White.Y) * luminance / White.Y;
        double y = luminance;

        double d = (Red.X * (Blue.Y - Green.Y)) + (Blue.X * (Green.Y - Red.Y)) + (Green.X * (Red.Y - Blue.Y));
        double sr = ((x * (Blue.Y - Green.Y)) - (Green.X * ((y * (Blue.Y - 1)) + (Blue.Y * (x + z)))) + (Blue.X * ((y * (Green.Y - 1)) + (Green.Y * (x + z))))) / d;
        double sg = ((x * (Red.Y - Blue.Y)) + (Red.X * ((y * (Blue.Y - 1)) + (Blue.Y * (x + z)))) - (Blue.X * ((y * (Red.Y - 1)) + (Red.Y * (x + z))))) / d;
        double sb = ((x * (Green.Y - Red.Y)) - (Red.X * ((y * (Green.Y - 1)) + (Green.Y * (x + z)))) + (Green.X * ((y * (Red.Y - 1)) + (Red.Y * (x + z))))) / d;

        return new M33(
            new D3(sr * Red.X, sr * Red.Y, sr * (1.0 - Red.X - Red.Y)),
            new D3(sg * Green.X, sg * Green.Y, sg * (1.0 - Green.X - Green.Y)),
            new D3(sb * Blue.X, sb * Blue.Y, sb * (1.0 - Blue.X - Blue.Y)));
    }

    /// <summary>XYZ to RGB as a row-vector matrix: the CTL's <c>XYZtoRGB_f33</c>.</summary>
    public M33 XyzToRgb(double luminance = 1.0) => RgbToXyz(luminance).Invert();

    /// <summary>
    /// RGB in these primaries to RGB in others, with the white adapted by Bradford when the two
    /// whites differ: the CTL's <c>calculate_rgb_to_rgb_matrix</c>.
    /// </summary>
    public M33 To(Chromaticities destination)
    {
        M33 cat = White == destination.White ? M33.Identity : Bradford(White, destination.White);
        return RgbToXyz() * cat * destination.XyzToRgb();
    }

    /// <summary>A Bradford chromatic adaptation between two whites, as a row-vector matrix.</summary>
    public static M33 Bradford((double X, double Y) source, (double X, double Y) destination)
    {
        var cone = new M33(new D3(0.89510, -0.75020, 0.03890), new D3(0.26640, 1.71350, -0.06850), new D3(-0.16140, 0.03670, 1.02960));

        static D3 Xyz((double X, double Y) xy) => new(xy.X / Math.Max(xy.Y, 1e-10), 1.0, (1.0 - xy.X - xy.Y) / Math.Max(xy.Y, 1e-10));

        D3 src = Xyz(source) * cone;
        D3 dst = Xyz(destination) * cone;
        return cone * M33.Diagonal(new D3(dst.X / src.X, dst.Y / src.Y, dst.Z / src.Z)) * cone.Invert();
    }
}
