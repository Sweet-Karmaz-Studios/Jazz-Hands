// The last pass: the premultiplied linear stack, laid over the background, encoded for where it
// is going. The preview and eight bit exports get BT.1886 with an ordered dither of half a code
// value, so a slow gradient does not band; ten bit and float outputs skip the dither.

#include "Common.hlsli"
#include "Color.hlsli"

#define ENCODE_BT1886 0
#define ENCODE_SRGB 1
#define ENCODE_LINEAR 2

cbuffer OutputConstants : register(b0)
{
    float4 Background;      // premultiplied linear
    uint Encoding;
    uint DitherLevels;      // 255 for eight bit, 1023 for ten, 0 for none
    uint KeepAlpha;         // 1 to write the stack's alpha rather than an opaque frame
    float Padding;
};

Texture2D<float4> Stack : register(t0);

FullScreenVertex VsMain(uint vertexId : SV_VertexID)
{
    return FullScreenTriangle(vertexId);
}

// 4x4 Bayer thresholds, centred on zero.
static const float Bayer[16] =
{
     0.0 / 16.0 - 0.5,  8.0 / 16.0 - 0.5,  2.0 / 16.0 - 0.5, 10.0 / 16.0 - 0.5,
    12.0 / 16.0 - 0.5,  4.0 / 16.0 - 0.5, 14.0 / 16.0 - 0.5,  6.0 / 16.0 - 0.5,
     3.0 / 16.0 - 0.5, 11.0 / 16.0 - 0.5,  1.0 / 16.0 - 0.5,  9.0 / 16.0 - 0.5,
    15.0 / 16.0 - 0.5,  7.0 / 16.0 - 0.5, 13.0 / 16.0 - 0.5,  5.0 / 16.0 - 0.5,
};

float4 PsMain(FullScreenVertex input) : SV_TARGET
{
    int2 pixel = int2(input.Position.xy);
    float4 stack = Stack.Load(int3(pixel, 0));

    float4 over = stack + Background * (1.0 - stack.a);
    float alpha = KeepAlpha != 0 ? over.a : 1.0;
    float3 colour = KeepAlpha != 0 && over.a > 1e-6 ? over.rgb / over.a : over.rgb;

    float3 encoded;
    switch (Encoding)
    {
        case ENCODE_SRGB:
            encoded = LinearToSrgb(colour);
            break;
        case ENCODE_LINEAR:
            encoded = colour;
            break;
        default:
            encoded = LinearToBt1886(colour);
            break;
    }

    if (DitherLevels != 0)
    {
        // Half a code value, well below what anyone can see and enough to break up banding.
        encoded += Bayer[(pixel.y & 3) * 4 + (pixel.x & 3)] / DitherLevels;
    }

    return float4(encoded, alpha);
}
