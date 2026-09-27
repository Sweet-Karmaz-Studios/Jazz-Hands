// An ACES project's input transform (Phase 44): a layer's picture into premultiplied ACEScg.
// The source pass has run with no transfer function (Transfer linear, primaries as they are), so
// t0 holds the stream's code values, premultiplied by alpha; a generator's layer arrives as the
// display light it was drawn in. Each kind decodes as AcesInput.cs does: a curve and a matrix to
// AP1, or, for a picture already made for a display, the output transform undone, so it shows
// as it went in. The constants for that inverse (SDR or HDR) are bound at b1 and t1.

#include "Common.hlsli"
#include "Color.hlsli"

#define ACES_CONSTANTS_REGISTER b1
#define ACES_TABLE_REGISTER t1
#include "Aces.hlsli"

#define INPUT_SRGB 0
#define INPUT_REC709 1
#define INPUT_LINEAR 2
#define INPUT_SLOG3 3
#define INPUT_LOGC3 4
#define INPUT_VLOG 5
#define INPUT_DISPLAY 6
#define INPUT_DISPLAY_LIGHT 7

cbuffer InputConstants : register(b0)
{
    float4 ToAp1[3];        // the camera's (or Rec.709's) linear primaries to AP1, rows
    uint Kind;
    uint3 InputPadding;
};

Texture2D<float4> Encoded : register(t0);

FullScreenVertex VsMain(uint vertexId : SV_VertexID)
{
    return FullScreenTriangle(vertexId);
}

float3 Decode(float3 x)
{
    switch (Kind)
    {
        case INPUT_SRGB:
            return x <= 0.04045 ? x / 12.92 : pow(max((x + 0.055) / 1.055, 0.0), 2.4);
        case INPUT_REC709:
        {
            // The BT.709 camera curve undone as ACES defines it: gamma 1/0.45, offset 0.099 and
            // the straight segment that meets it smoothly.
            const float gamma = 1.0 / 0.45;
            const float breakCode = 0.099 / (gamma - 1.0);
            const float breakLinear = pow((breakCode + 0.099) / 1.099, gamma);
            return x < breakCode ? x * breakLinear / breakCode : pow(max((x + 0.099) / 1.099, 0.0), gamma);
        }
        case INPUT_SLOG3:
            return x >= 171.2102946929 / 1023.0
                ? pow(10.0, (x * 1023.0 - 420.0) / 261.5) * 0.19 - 0.01
                : (x * 1023.0 - 95.0) * 0.01125 / (171.2102946929 - 95.0);
        case INPUT_LOGC3:
            return x > 5.367655 * 0.010591 + 0.092809
                ? (pow(10.0, (x - 0.385537) / 0.247190) - 0.052272) / 5.555556
                : (x - 0.092809) / 5.367655;
        case INPUT_VLOG:
            return x < 0.181 ? (x - 0.125) / 5.6 : pow(10.0, (x - 0.598206) / 0.241514) - 0.00873;
        default:
            return x;
    }
}

float4 PsMain(FullScreenVertex input) : SV_TARGET
{
    float4 c = Encoded.Load(int3(input.Position.xy, 0));
    if (c.a <= 1e-6)
    {
        return 0.0;
    }

    float3 code = c.rgb / c.a;
    float3 ap1;
    if (Kind == INPUT_DISPLAY || Kind == INPUT_DISPLAY_LIGHT)
    {
        // Display light is BT.1886 encoded first: the SDR output transform's own encoding.
        float3 display = Kind == INPUT_DISPLAY_LIGHT ? LinearToBt1886(saturate(code)) : code;
        ap1 = RowMul(AcesOutputInverse(display), Ap0ToAp1);
    }
    else
    {
        ap1 = RowMul(Decode(code), ToAp1);
    }

    return float4(ap1 * c.a, c.a);
}
