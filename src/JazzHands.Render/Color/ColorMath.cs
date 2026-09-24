using System.Numerics;

namespace JazzHands.Render.Color;

/// <summary>
/// White balance maths done once per frame on the CPU: chromaticities, XYZ and the Bradford
/// chromatic adaptation, handed to the shader as one 3x3 matrix on linear BT.709.
/// </summary>
public static class ColorMath
{
    /// <summary>The D65 white point, BT.709's and the working space's.</summary>
    public static readonly Vector2 D65 = new(0.3127f, 0.3290f);

    /// <summary>Linear BT.709 to CIE XYZ (D65).</summary>
    public static readonly Matrix4x4 Rgb709ToXyz = new(
        0.4124564f, 0.3575761f, 0.1804375f, 0,
        0.2126729f, 0.7151522f, 0.0721750f, 0,
        0.0193339f, 0.1191920f, 0.9503041f, 0,
        0, 0, 0, 1);

    /// <summary>The Bradford cone response matrix.</summary>
    private static readonly Matrix4x4 Bradford = new(
        0.8951f, 0.2664f, -0.1614f, 0,
        -0.7502f, 1.7135f, 0.0367f, 0,
        0.0389f, -0.0685f, 1.0296f, 0,
        0, 0, 0, 1);

    /// <summary>CIE XYZ to linear BT.709.</summary>
    public static Matrix4x4 XyzToRgb709 { get; } = Invert(Rgb709ToXyz);

    /// <summary>A chromaticity as XYZ with Y = 1.</summary>
    public static Vector3 XyzOf(Vector2 xy) =>
        xy.Y <= 1e-6f ? new Vector3(0.9505f, 1.0f, 1.0891f) : new Vector3(xy.X / xy.Y, 1.0f, (1.0f - xy.X - xy.Y) / xy.Y);

    /// <summary>
    /// The chromaticity of CIE daylight at a correlated colour temperature, 4000 K to 25000 K
    /// (held inside), from the CIE's polynomial for the daylight locus.
    /// </summary>
    public static Vector2 DaylightXy(double kelvin)
    {
        double t = Math.Clamp(kelvin, 4000.0, 25000.0);
        double x = t <= 7000.0
            ? (-4.6070e9 / (t * t * t)) + (2.9678e6 / (t * t)) + (0.09911e3 / t) + 0.244063
            : (-2.0064e9 / (t * t * t)) + (1.9018e6 / (t * t)) + (0.24748e3 / t) + 0.237040;
        double y = (-3.0 * x * x) + (2.870 * x) - 0.275;
        return new Vector2((float)x, (float)y);
    }

    /// <summary>
    /// A matrix on linear BT.709 that adapts colours seen under <paramref name="sourceWhite"/> to
    /// how they look under <paramref name="targetWhite"/>, by Bradford: what was the source white
    /// becomes the target white, and everything else moves with it the way the eye adapts.
    /// </summary>
    public static Matrix4x4 Adaptation(Vector3 sourceWhite, Vector3 targetWhite)
    {
        Vector3 source = Apply(Bradford, sourceWhite);
        Vector3 target = Apply(Bradford, targetWhite);
        Matrix4x4 scale = Matrix4x4.Identity;
        scale.M11 = target.X / MathF.Max(source.X, 1e-6f);
        scale.M22 = target.Y / MathF.Max(source.Y, 1e-6f);
        scale.M33 = target.Z / MathF.Max(source.Z, 1e-6f);

        // These matrices are column transforms (m . v), composed as the maths reads:
        // XYZ to RGB . Bradford inverse . scale . Bradford . RGB to XYZ.
        return Multiply(XyzToRgb709, Multiply(Invert(Bradford), Multiply(scale, Multiply(Bradford, Rgb709ToXyz))));
    }

    /// <summary>
    /// The white balance for a temperature and tint slider, each -100 to 100: positive
    /// temperature warms, positive tint pushes towards magenta. Zero for both is exactly the identity.
    /// </summary>
    public static Matrix4x4 TemperatureTint(float temperature, float tint)
    {
        if (temperature == 0 && tint == 0)
        {
            return Matrix4x4.Identity;
        }

        // Warming the picture is what adapting from a bluer white does: the slider moves the
        // white it adapts from along the daylight locus in mireds, 4370 K to 12700 K, measured
        // from D65 itself so the middle is exact. Tint moves it off the locus, green for magenta.
        double mired = (1e6 / 6504.0) - (temperature * 0.75);
        Vector2 shift = DaylightXy(1e6 / mired) - DaylightXy(6504.0);
        Vector2 white = D65 + shift + new Vector2(0, tint * 0.0002f);

        return Adaptation(XyzOf(white), XyzOf(D65));
    }

    /// <summary>
    /// The white balance that makes a colour, linear BT.709, neutral: its chromaticity adapted to D65.
    /// </summary>
    public static Matrix4x4 Neutralise(Vector3 linear)
    {
        Vector3 xyz = Apply(Rgb709ToXyz, linear);
        float sum = xyz.X + xyz.Y + xyz.Z;
        if (sum <= 1e-6f || linear.X <= 0 || linear.Y <= 0 || linear.Z <= 0)
        {
            return Matrix4x4.Identity;
        }

        var xy = new Vector2(xyz.X / sum, xyz.Y / sum);
        return Adaptation(XyzOf(xy), XyzOf(D65));
    }

    /// <summary>m . v for a matrix written with its rows as M1x, M2x, M3x.</summary>
    public static Vector3 Apply(Matrix4x4 m, Vector3 v) => new(
        (m.M11 * v.X) + (m.M12 * v.Y) + (m.M13 * v.Z),
        (m.M21 * v.X) + (m.M22 * v.Y) + (m.M23 * v.Z),
        (m.M31 * v.X) + (m.M32 * v.Y) + (m.M33 * v.Z));

    /// <summary>The three rows of a 3x3 matrix as float4s, for the shader's Transform.</summary>
    public static (Vector4 Row0, Vector4 Row1, Vector4 Row2) Rows(Matrix4x4 m) =>
        (new Vector4(m.M11, m.M12, m.M13, 0), new Vector4(m.M21, m.M22, m.M23, 0), new Vector4(m.M31, m.M32, m.M33, 0));

    /// <summary>(a . b) as column transforms: the matrix that applies b, then a.</summary>
    private static Matrix4x4 Multiply(Matrix4x4 a, Matrix4x4 b)
    {
        var result = new Matrix4x4();
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                float sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += a[row, k] * b[k, column];
                }

                result[row, column] = sum;
            }
        }

        return result;
    }

    private static Matrix4x4 Invert(Matrix4x4 m) =>
        Matrix4x4.Invert(m, out Matrix4x4 inverse) ? inverse : throw new InvalidOperationException("The matrix has no inverse.");
}
