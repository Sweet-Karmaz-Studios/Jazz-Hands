// Transfer functions and gamut conversion, one function each, so every pass converts the same
// way. See the color-science skill for which one applies where.

#ifndef JAZZ_COLOR_HLSLI
#define JAZZ_COLOR_HLSLI

// Transfer function codes, matching TransferFunction in C#.
#define TRANSFER_BT709 0
#define TRANSFER_PQ 1
#define TRANSFER_HLG 2
#define TRANSFER_LINEAR 3
#define TRANSFER_SRGB 4

// BT.1886: the display gamma an SDR video signal is mastered for. Decoding with this and encoding
// with its inverse is the identity, so SDR video passes through the working space unchanged.
float3 Bt1886ToLinear(float3 encoded)
{
    return pow(max(encoded, 0.0), 2.4);
}

float3 LinearToBt1886(float3 linearLight)
{
    return pow(max(linearLight, 0.0), 1.0 / 2.4);
}

// IEC 61966-2-1, for PNG, JPEG and other stills.
float3 SrgbToLinear(float3 encoded)
{
    encoded = max(encoded, 0.0);
    return encoded <= 0.04045 ? encoded / 12.92 : pow((encoded + 0.055) / 1.055, 2.4);
}

float3 LinearToSrgb(float3 linearLight)
{
    linearLight = max(linearLight, 0.0);
    return linearLight <= 0.0031308 ? linearLight * 12.92 : 1.055 * pow(linearLight, 1.0 / 2.4) - 0.055;
}

// SMPTE ST 2084 to absolute luminance in nits.
float3 PqToNits(float3 encoded)
{
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;

    float3 e = pow(max(encoded, 0.0), 1.0 / m2);
    return 10000.0 * pow(max(e - c1, 0.0) / max(c2 - c3 * e, 1e-6), 1.0 / m1);
}

// ARIB STD-B67 to display luminance in nits, for a 1000 nit display with system gamma 1.2.
float3 HlgToNits(float3 encoded)
{
    const float a = 0.17883277;
    const float b = 0.28466892;
    const float c = 0.55991073;

    float3 scene = encoded <= 0.5 ? (encoded * encoded) / 3.0 : (exp((encoded - c) / a) + b) / 12.0;
    float luminance = dot(scene, float3(0.2627, 0.6780, 0.0593));
    return 1000.0 * scene * pow(max(luminance, 1e-6), 0.2);
}

// Primaries codes, matching ColorPrimaries in C#.
#define PRIMARIES_BT709 0
#define PRIMARIES_BT2020 1

// Tone map operators, matching ToneMapOperator in C#.
#define TONEMAP_BT2390 0
#define TONEMAP_HABLE 1
#define TONEMAP_MOBIUS 2
#define TONEMAP_CLIP 3

// HDR's reference white (BT.2408) is SDR's white: 203 nits is 1.0 in the working space. The
// skill's 100 nit target is the same thing in SDR's units; mapping to 203 keeps a graded HDR
// face as bright in the SDR result as an SDR grade would have it.
static const float ReferenceWhiteNits = 203.0;

float3 Bt2020ToBt709(float3 rgb)
{
    return float3(
        dot(rgb, float3(1.6605, -0.5876, -0.0728)),
        dot(rgb, float3(-0.1246, 1.1329, -0.0083)),
        dot(rgb, float3(-0.0182, -0.1006, 1.1187)));
}

// A colour outside BT.709 after the conversion has a negative channel. Clamping that channel
// shifts the hue and brightens it; this pulls the colour towards its own luminance just far
// enough that the lowest channel is zero, which keeps both.
float3 GamutClip(float3 rgb)
{
    float luminance = max(dot(rgb, float3(0.2126, 0.7152, 0.0722)), 0.0);
    float lowest = min(rgb.r, min(rgb.g, rgb.b));
    if (lowest >= 0.0)
    {
        return rgb;
    }

    float keep = luminance / max(luminance - lowest, 1e-6);
    return max(lerp(luminance.xxx, rgb, keep), 0.0);
}

// Absolute luminance to a PQ code value, 0 to 1: the inverse of PqToNits, for one value.
float NitsToPq(float nits)
{
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;

    float y = pow(saturate(nits / 10000.0), m1);
    return pow((c1 + c2 * y) / (1.0 + c3 * y), m2);
}

// BT.2390's EETF from a source peak to SDR white, in nits. Below the knee (KS) the signal is
// untouched in PQ, so shadows and mid tones stay where the grade put them; above it a Hermite
// spline rolls off to exactly white at the source's peak. The knee sits lower the brighter the
// source is.
float Bt2390(float nits, float peakNits)
{
    float source = NitsToPq(peakNits);
    float maxLum = NitsToPq(ReferenceWhiteNits) / source;
    if (maxLum >= 1.0)
    {
        return nits;
    }

    float e1 = min(NitsToPq(nits) / source, 1.0);
    float ks = 1.5 * maxLum - 0.5;
    float e2 = e1;

    if (e1 > ks)
    {
        float t = (e1 - ks) / (1.0 - ks);
        float t2 = t * t;
        float t3 = t2 * t;
        e2 = (2.0 * t3 - 3.0 * t2 + 1.0) * ks + (t3 - 2.0 * t2 + t) * (1.0 - ks) + (-2.0 * t3 + 3.0 * t2) * maxLum;
    }

    return PqToNits((e2 * source).xxx).x;
}

float HableCurve(float x)
{
    const float a = 0.15;
    const float b = 0.50;
    const float c = 0.10;
    const float d = 0.20;
    const float e = 0.02;
    const float f = 0.30;
    return ((x * (a * x + c * b) + d * e) / (x * (a * x + b) + d * f)) - e / f;
}

// mpv's Möbius: the identity up to j, then a curve reaching 1 at the peak with a matching slope.
float Mobius(float x, float peak)
{
    const float j = 0.3;
    if (x <= j || peak <= 1.0)
    {
        return min(x, max(peak, 1.0));
    }

    float a = -j * j * (peak - 1.0) / (j * j - 2.0 * j + peak);
    float b = (j * j - 2.0 * j * peak + peak) / max(peak - 1.0, 1e-6);
    return (b * b + 2.0 * b * j + j * j) / (b - a) * (x + a) / (x + b);
}

// HDR in BT.2020 nits to BT.2020 relative to SDR white. The curve runs on the largest channel,
// which keeps hue and saturation where a curve per channel would bend them; then the highlights
// it compressed lose some colour, by as much as they were compressed, so a bright saturated sky
// does not come out neon.
float3 ToneMap(float3 nits, uint op, float peakNits, float desaturate)
{
    float peak = max(peakNits, ReferenceWhiteNits) / ReferenceWhiteNits;
    float3 relative = max(nits, 0.0) / ReferenceWhiteNits;
    float signal = max(relative.r, max(relative.g, relative.b));
    if (signal <= 1e-6)
    {
        return 0.0;
    }

    float mapped;
    switch (op)
    {
        case TONEMAP_HABLE:
            mapped = HableCurve(min(signal, peak)) / HableCurve(peak);
            break;
        case TONEMAP_MOBIUS:
            mapped = Mobius(min(signal, peak), peak);
            break;
        case TONEMAP_CLIP:
            mapped = min(signal, 1.0);
            break;
        default:
            mapped = Bt2390(signal * ReferenceWhiteNits, peakNits) / ReferenceWhiteNits;
            break;
    }

    float3 rgb = relative * (mapped / signal);
    float compressed = saturate(1.0 - mapped / signal);
    float luminance = dot(rgb, float3(0.2627, 0.6780, 0.0593));
    return lerp(rgb, luminance.xxx, saturate(desaturate) * compressed);
}

// Any encoded signal to linear BT.709 light, with SDR white at 1. HDR is tone mapped in its own
// BT.2020 before the primaries are converted, so the curve sees the colours the grade had.
float3 ToLinear(float3 encoded, uint transfer, uint primaries, uint toneOperator, float peakNits, float desaturate)
{
    float3 rgb;
    switch (transfer)
    {
        case TRANSFER_PQ:
            rgb = ToneMap(PqToNits(encoded), toneOperator, peakNits, desaturate);
            break;
        case TRANSFER_HLG:
            rgb = ToneMap(HlgToNits(encoded), toneOperator, peakNits, desaturate);
            break;
        case TRANSFER_LINEAR:
            rgb = encoded;
            break;
        case TRANSFER_SRGB:
            rgb = SrgbToLinear(encoded);
            break;
        default:
            rgb = Bt1886ToLinear(encoded);
            break;
    }

    return primaries == PRIMARIES_BT2020 ? GamutClip(Bt2020ToBt709(rgb)) : rgb;
}

#endif
