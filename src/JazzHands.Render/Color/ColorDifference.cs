using System.Numerics;

namespace JazzHands.Render.Color;

/// <summary>
/// How far apart two colours look: CIELAB and CIEDE2000 for the number people quote, Oklab for
/// judging colourfulness, and the sRGB curve the grading shaders work on, as the shaders have it.
/// </summary>
public static class ColorDifference
{
    private static readonly Vector3 WhiteD65 = new(0.95047f, 1.0f, 1.08883f);

    /// <summary>Linear light to the sRGB-encoded values the colourist's effects work on (Color.hlsli's LinearToSrgb).</summary>
    public static float ToSrgb(float linear)
    {
        linear = MathF.Max(linear, 0);
        return linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1 / 2.4f)) - 0.055f;
    }

    /// <summary>sRGB-encoded values back to linear light.</summary>
    public static float FromSrgb(float encoded)
    {
        encoded = Math.Clamp(encoded, 0, 1);
        return encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>Linear BT.709 to CIELAB under D65.</summary>
    public static Vector3 Lab(Vector3 linear)
    {
        Vector3 xyz = ColorMath.Apply(ColorMath.Rgb709ToXyz, Vector3.Max(linear, Vector3.Zero)) / WhiteD65;

        static float F(float t) => t > 216f / 24389f ? MathF.Cbrt(t) : ((24389f / 27f * t) + 16) / 116;

        float fx = F(xyz.X), fy = F(xyz.Y), fz = F(xyz.Z);
        return new Vector3((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>Linear BT.709 to Oklab (Björn Ottosson's matrices).</summary>
    public static Vector3 Oklab(Vector3 linear)
    {
        linear = Vector3.Max(linear, Vector3.Zero);
        float l = MathF.Cbrt((0.4122214708f * linear.X) + (0.5363325363f * linear.Y) + (0.0514459929f * linear.Z));
        float m = MathF.Cbrt((0.2119034982f * linear.X) + (0.6806995451f * linear.Y) + (0.1073969566f * linear.Z));
        float s = MathF.Cbrt((0.0883024619f * linear.X) + (0.2817188376f * linear.Y) + (0.6299787005f * linear.Z));
        return new Vector3(
            (0.2104542553f * l) + (0.7936177850f * m) - (0.0040720468f * s),
            (1.9779984951f * l) - (2.4285922050f * m) + (0.4505937099f * s),
            (0.0259040371f * l) + (0.7827717662f * m) - (0.8086757660f * s));
    }

    /// <summary>How colourful a linear BT.709 colour is: its Oklab chroma.</summary>
    public static float Chroma(Vector3 linear)
    {
        Vector3 lab = Oklab(linear);
        return MathF.Sqrt((lab.Y * lab.Y) + (lab.Z * lab.Z));
    }

    /// <summary>
    /// The CIEDE2000 difference between two CIELAB colours: about 1 is the smallest difference
    /// most people can see side by side, 2 is close enough that a cut between them does not jump.
    /// </summary>
    public static double DeltaE2000(Vector3 first, Vector3 second)
    {
        double l1 = first.X, a1 = first.Y, b1 = first.Z;
        double l2 = second.X, a2 = second.Y, b2 = second.Z;

        double c1 = Math.Sqrt((a1 * a1) + (b1 * b1));
        double c2 = Math.Sqrt((a2 * a2) + (b2 * b2));
        double meanC = (c1 + c2) / 2;
        double meanC7 = Math.Pow(meanC, 7);
        double g = 0.5 * (1 - Math.Sqrt(meanC7 / (meanC7 + Math.Pow(25, 7))));

        double a1p = (1 + g) * a1, a2p = (1 + g) * a2;
        double c1p = Math.Sqrt((a1p * a1p) + (b1 * b1));
        double c2p = Math.Sqrt((a2p * a2p) + (b2 * b2));
        double h1p = Hue(b1, a1p), h2p = Hue(b2, a2p);

        double dL = l2 - l1;
        double dC = c2p - c1p;
        double dh = c1p * c2p == 0 ? 0 : h2p - h1p;
        if (dh > 180)
        {
            dh -= 360;
        }
        else if (dh < -180)
        {
            dh += 360;
        }

        double dH = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Radians(dh / 2));

        double meanL = (l1 + l2) / 2;
        double meanCp = (c1p + c2p) / 2;
        double meanH = h1p + h2p;
        if (c1p * c2p != 0)
        {
            meanH = Math.Abs(h1p - h2p) <= 180 ? meanH / 2 : (meanH + (meanH < 360 ? 360 : -360)) / 2;
        }

        double t = 1 - (0.17 * Math.Cos(Radians(meanH - 30))) + (0.24 * Math.Cos(Radians(2 * meanH)))
            + (0.32 * Math.Cos(Radians((3 * meanH) + 6))) - (0.20 * Math.Cos(Radians((4 * meanH) - 63)));
        double dTheta = 30 * Math.Exp(-Math.Pow((meanH - 275) / 25, 2));
        double meanCp7 = Math.Pow(meanCp, 7);
        double rC = 2 * Math.Sqrt(meanCp7 / (meanCp7 + Math.Pow(25, 7)));
        double lm50 = (meanL - 50) * (meanL - 50);
        double sL = 1 + (0.015 * lm50 / Math.Sqrt(20 + lm50));
        double sC = 1 + (0.045 * meanCp);
        double sH = 1 + (0.015 * meanCp * t);
        double rT = -Math.Sin(Radians(2 * dTheta)) * rC;

        double termL = dL / sL, termC = dC / sC, termH = dH / sH;
        return Math.Sqrt((termL * termL) + (termC * termC) + (termH * termH) + (rT * termC * termH));

        static double Hue(double b, double a)
        {
            if (a == 0 && b == 0)
            {
                return 0;
            }

            double degrees = Math.Atan2(b, a) * 180 / Math.PI;
            return degrees < 0 ? degrees + 360 : degrees;
        }

        static double Radians(double degrees) => degrees * Math.PI / 180;
    }

    /// <summary>
    /// The mean CIEDE2000 difference, pixel by pixel, between two frames of premultiplied linear
    /// BT.709 (four floats a pixel) the same size, over every <paramref name="step"/>th pixel.
    /// </summary>
    public static double MeanDeltaE(ReadOnlySpan<float> first, ReadOnlySpan<float> second, int step = 1)
    {
        if (first.Length != second.Length || first.Length % 4 != 0)
        {
            throw new ArgumentException("The frames are not the same size.", nameof(second));
        }

        double sum = 0;
        int count = 0;
        for (int pixel = 0; pixel < first.Length / 4; pixel += Math.Max(step, 1))
        {
            sum += DeltaE2000(Lab(Straight(first, pixel)), Lab(Straight(second, pixel)));
            count++;
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>One pixel's straight linear colour out of a premultiplied frame.</summary>
    public static Vector3 Straight(ReadOnlySpan<float> premultiplied, int pixel)
    {
        int at = pixel * 4;
        float alpha = premultiplied[at + 3];
        var rgb = new Vector3(premultiplied[at], premultiplied[at + 1], premultiplied[at + 2]);
        return alpha > 1e-6f ? rgb / alpha : Vector3.Zero;
    }
}
