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

float3 Bt2020ToBt709(float3 rgb)
{
    return float3(
        dot(rgb, float3(1.6605, -0.5876, -0.0728)),
        dot(rgb, float3(-0.1246, 1.1329, -0.0083)),
        dot(rgb, float3(-0.0182, -0.1006, 1.1187)));
}

// The tone map hook. Phase 17 replaces the body with BT.2390 and the alternatives; everything
// that calls it stays as it is. Input is BT.2020 nits, output is linear BT.709 with reference
// white (203 nits) at 1.
float3 ToneMap(float3 nits)
{
    const float white = 1000.0 / 203.0;

    float3 relative = nits / 203.0;
    float luminance = dot(relative, float3(0.2627, 0.6780, 0.0593));
    float mapped = luminance * (1.0 + luminance / (white * white)) / (1.0 + luminance);
    return saturate(Bt2020ToBt709(relative * (mapped / max(luminance, 1e-6))));
}

// Any encoded signal to linear BT.709 light, with SDR white at 1.
float3 ToLinear(float3 encoded, uint transfer)
{
    switch (transfer)
    {
        case TRANSFER_PQ:
            return ToneMap(PqToNits(encoded));
        case TRANSFER_HLG:
            return ToneMap(HlgToNits(encoded));
        case TRANSFER_LINEAR:
            return encoded;
        case TRANSFER_SRGB:
            return SrgbToLinear(encoded);
        default:
            return Bt1886ToLinear(encoded);
    }
}

#endif
