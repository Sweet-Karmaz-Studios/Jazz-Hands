// The last pass for an encoder: the premultiplied linear stack, laid over the background, encoded
// with BT.1886 exactly as Output.hlsl does, then turned into BT.709 limited range Y'CbCr and split
// into the planes an NV12 frame is made of. PsLuma writes Y' into an R8 target at the frame size;
// PsChroma writes Cb and Cr into an R8G8 target at half the size each way, each sample the mean of
// the two by two block of pixels it covers.
//
// Eight bit targets store n/255, so the limited range codes are written as 16/255 to 235/255 for
// luma and 16/255 to 240/255 for chroma, with neutral chroma at 128/255. For a ten bit encode the
// targets are R16 and R16G16 and the codes are 64 to 940 and 64 to 960, laid out as P010.

#include "Common.hlsli"
#include "Color.hlsli"

#define ENCODE_BT1886 0
#define ENCODE_SRGB 1
#define ENCODE_LINEAR 2

cbuffer YuvConstants : register(b0)
{
    float4 Background;      // premultiplied linear
    uint Encoding;
    uint DitherLevels;      // 255 for eight bit, 1023 for ten, 0 for none
    uint2 LumaSize;         // the luma target: where a chroma sample's four pixels are
    uint Bits;              // 8 for R8 targets (NV12), 10 for R16 targets (P010)
    uint3 Pad;
};

Texture2D<float4> Stack : register(t0);

FullScreenVertex VsMain(uint vertexId : SV_VertexID)
{
    return FullScreenTriangle(vertexId);
}

// 4x4 Bayer thresholds, centred on zero, as Output.hlsl has them.
static const float Bayer[16] =
{
     0.0 / 16.0 - 0.5,  8.0 / 16.0 - 0.5,  2.0 / 16.0 - 0.5, 10.0 / 16.0 - 0.5,
    12.0 / 16.0 - 0.5,  4.0 / 16.0 - 0.5, 14.0 / 16.0 - 0.5,  6.0 / 16.0 - 0.5,
     3.0 / 16.0 - 0.5, 11.0 / 16.0 - 0.5,  1.0 / 16.0 - 0.5,  9.0 / 16.0 - 0.5,
    15.0 / 16.0 - 0.5,  7.0 / 16.0 - 0.5, 13.0 / 16.0 - 0.5,  5.0 / 16.0 - 0.5,
};

// The encoded R'G'B' of one luma-grid pixel, read from the stack at the matching place. The stack
// is the size of the luma target except when rounding the export size moved it by a pixel.
float3 EncodedAt(int2 pixel)
{
    uint stackWidth;
    uint stackHeight;
    Stack.GetDimensions(stackWidth, stackHeight);

    int2 source = int2(float2(pixel) * float2(stackWidth, stackHeight) / float2(LumaSize));
    source = clamp(source, int2(0, 0), int2(stackWidth, stackHeight) - 1);

    float4 stack = Stack.Load(int3(source, 0));
    float3 colour = stack.rgb + Background.rgb * (1.0 - stack.a);

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
        encoded += Bayer[(pixel.y & 3) * 4 + (pixel.x & 3)] / DitherLevels;
    }

    return saturate(encoded);
}

float LumaOf(float3 rgb)
{
    return dot(rgb, float3(0.2126, 0.7152, 0.0722));
}

// A sixteen bit target stores n/65535, and P010 keeps the ten bit code in the top ten bits, so
// code c is written as c * 64 / 65535, which the target rounds back to exactly c * 64.
float TenBit(float code)
{
    return round(code) * 64.0 / 65535.0;
}

float PsLuma(FullScreenVertex input) : SV_TARGET
{
    float y = LumaOf(EncodedAt(int2(input.Position.xy)));
    if (Bits == 10)
    {
        return TenBit(64.0 + 876.0 * y);
    }

    return (16.0 + 219.0 * y) / 255.0;
}

float2 PsChroma(FullScreenVertex input) : SV_TARGET
{
    int2 origin = int2(input.Position.xy) * 2;

    float3 sum = EncodedAt(origin)
        + EncodedAt(origin + int2(1, 0))
        + EncodedAt(origin + int2(0, 1))
        + EncodedAt(origin + int2(1, 1));
    float3 rgb = sum * 0.25;

    float y = LumaOf(rgb);
    float cb = (rgb.b - y) / 1.8556;
    float cr = (rgb.r - y) / 1.5748;

    if (Bits == 10)
    {
        return float2(TenBit(512.0 + 896.0 * cb), TenBit(512.0 + 896.0 * cr));
    }

    return float2((128.0 + 224.0 * cb) / 255.0, (128.0 + 224.0 * cr) / 255.0);
}
