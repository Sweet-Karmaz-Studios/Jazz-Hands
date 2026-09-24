// What every effect pass shares: Common.hlsli, Color.hlsli, the constants EffectContext.Draw binds
// at b0, the full-screen triangle, and the helpers most effects need. An effect's own constants go
// in a cbuffer at b1 and its inputs from t0. Inputs and outputs are premultiplied linear light,
// frame sized at the working resolution. Pixel-sized parameters are sequence pixels; multiply by
// QualityScale for texels, so the preview at half size matches the export.

#ifndef JAZZ_EFFECT_HLSLI
#define JAZZ_EFFECT_HLSLI

#include "Common.hlsli"
#include "Color.hlsli"

cbuffer EffectCommon : register(b0)
{
    float2 TexelSize;       // one texel of the target, in texture coordinates
    float2 Resolution;      // the target in texels
    float Time;             // seconds from the effect's owner's start
    float QualityScale;     // target texels per sequence pixel
    float2 EffectCommonPadding;
};

FullScreenVertex VsMain(uint vertexId : SV_VertexID)
{
    return FullScreenTriangle(vertexId);
}

// Straight colour in a perceptual (sRGB) encoding, for effects whose parameters are about what a
// person sees: posterize steps, grain, keys and levels read in that space as they do in every
// other editor. Alpha comes back unchanged.
float4 ToPerceptual(float4 premultiplied)
{
    float4 straight = Unpremultiply(premultiplied);
    return float4(LinearToSrgb(max(straight.rgb, 0.0)), straight.a);
}

// The way back: perceptual straight colour to premultiplied linear light.
float4 FromPerceptual(float4 perceptual)
{
    return Premultiply(float4(SrgbToLinear(saturate(perceptual.rgb)), saturate(perceptual.a)));
}

// Sequence pixels from the frame centre to a texture coordinate, and back.
float2 PointToUv(float2 fromCentre)
{
    return 0.5 + fromCentre * QualityScale * TexelSize;
}

float2 UvToPoint(float2 uv)
{
    return (uv - 0.5) * Resolution / QualityScale;
}

// A texture coordinate one texel is: the step for a pixel-sized parameter.
float2 PixelsToUv(float2 sequencePixels)
{
    return sequencePixels * QualityScale * TexelSize;
}

float2 Rotate(float2 v, float radians)
{
    float s = sin(radians);
    float c = cos(radians);
    return float2(v.x * c - v.y * s, v.x * s + v.y * c);
}

// A repeatable pseudo random number from a position and a seed, 0 to 1.
float Random(float2 p, float seed)
{
    return Hash(p + float2(seed * 0.1234, seed * 0.5678));
}

#endif
