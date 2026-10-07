namespace JazzHands.Media.Thumbnails;

/// <summary>
/// Brings an HDR picture down to SDR for a thumbnail, on the CPU: thumbnails are small, and the
/// GPU tone mapper belongs to the compositor, which a thumbnail worker does not have.
/// </summary>
/// <remarks>
/// The code values are decoded to light (PQ by ST 2084; HLG by its inverse OETF and a 1000 nit
/// display's system gamma of 1.2), taken from BT.2020 to BT.709 primaries, scaled so the 203 nit
/// reference white of BT.2408 is 1, compressed by extended Reinhard on luminance with 1000 nits at
/// white, and encoded sRGB. A thumbnail is to recognise a shot by; this is close to what the
/// preview's tone mapper shows, not equal to it.
/// </remarks>
public static class HdrThumbnail
{
    /// <summary>Light at the reference white, in nits.</summary>
    public const double ReferenceWhite = 203.0;

    /// <summary>The light that comes out at white, in nits.</summary>
    public const double Peak = 1000.0;

    /// <summary>True for a transfer this brings down: PQ or HLG.</summary>
    public static bool Handles(string transfer) => transfer is "smpte2084" or "arib-std-b67";

    /// <summary>Tone maps 16-bit RGB code values, three to a pixel, into 8-bit sRGB.</summary>
    /// <param name="codes">BT.2020 code values, 0 to 65535, R G B per pixel.</param>
    /// <param name="srgb">Where the 8-bit sRGB values go, R G B per pixel.</param>
    /// <param name="transfer">The source's transfer: smpte2084 or arib-std-b67.</param>
    public static void ToneMap(ReadOnlySpan<ushort> codes, Span<byte> srgb, string transfer)
    {
        if (srgb.Length < codes.Length)
        {
            throw new ArgumentException("The output is smaller than the input.", nameof(srgb));
        }

        bool hlg = transfer == "arib-std-b67";
        double white = Peak / ReferenceWhite;
        for (int index = 0; index + 2 < codes.Length; index += 3)
        {
            double r = Light(codes[index] / 65535.0, hlg);
            double g = Light(codes[index + 1] / 65535.0, hlg);
            double b = Light(codes[index + 2] / 65535.0, hlg);

            // BT.2020 to BT.709 primaries, in linear light.
            double r709 = (1.6605 * r) - (0.5876 * g) - (0.0728 * b);
            double g709 = (-0.1246 * r) + (1.1329 * g) - (0.0083 * b);
            double b709 = (-0.0182 * r) - (0.1006 * g) + (1.1187 * b);

            double luminance = Math.Max((0.2126 * r709) + (0.7152 * g709) + (0.0722 * b709), 0);
            double scale = luminance > 0 ? luminance * (1 + (luminance / (white * white))) / (1 + luminance) / luminance : 0;

            srgb[index] = Encode(r709 * scale);
            srgb[index + 1] = Encode(g709 * scale);
            srgb[index + 2] = Encode(b709 * scale);
        }
    }

    /// <summary>A code value as light, 1 at the reference white.</summary>
    private static double Light(double code, bool hlg)
    {
        double nits;
        if (hlg)
        {
            // Inverse OETF to scene light, 0 to 1, then the display's system gamma.
            const double A = 0.17883277, B = 0.28466892, C = 0.55991073;
            double scene = code <= 0.5 ? code * code / 3.0 : (Math.Exp((code - C) / A) + B) / 12.0;
            nits = Peak * Math.Pow(scene, 1.2);
        }
        else
        {
            const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;
            double power = Math.Pow(code, 1 / M2);
            nits = 10000 * Math.Pow(Math.Max(power - C1, 0) / (C2 - (C3 * power)), 1 / M1);
        }

        return nits / ReferenceWhite;
    }

    private static byte Encode(double linear)
    {
        double value = Math.Clamp(linear, 0, 1);
        double encoded = value <= 0.0031308 ? value * 12.92 : (1.055 * Math.Pow(value, 1 / 2.4)) - 0.055;
        return (byte)Math.Round(encoded * 255);
    }
}
