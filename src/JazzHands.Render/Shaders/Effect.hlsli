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
    uint WorkingSpace;      // 0 display referred (linear BT.709), 1 ACES (ACEScg), Phase 44
    float EffectCommonPadding;
};

FullScreenVertex VsMain(uint vertexId : SV_VertexID)
{
    return FullScreenTriangle(vertexId);
}

// Straight colour in a perceptual encoding, for effects whose parameters are about what a person
// sees: posterize steps, grain, keys and levels read in that space as they do in every other
// editor. sRGB in a display-referred project; ACEScct in an ACES one (Phase 44), which is what a
// colourist grades in there. Alpha comes back unchanged.
float4 ToPerceptual(float4 premultiplied)
{
    float4 straight = Unpremultiply(premultiplied);
    float3 rgb = max(straight.rgb, 0.0);
    return float4(WorkingSpace == 1 ? LinearToAcescct(rgb) : LinearToSrgb(rgb), straight.a);
}

// The way back: perceptual straight colour to premultiplied linear light. ACEScct runs past 1
// for scene light brighter than diffuse white, so it is held at its own top, not at 1.
float4 FromPerceptual(float4 perceptual)
{
    float3 rgb = WorkingSpace == 1
        ? AcescctToLinear(clamp(perceptual.rgb, 0.0, 1.4679964))
        : SrgbToLinear(saturate(perceptual.rgb));
    return Premultiply(float4(rgb, saturate(perceptual.a)));
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

// A repeatable pseudo random number from 0 to 1 for a whole-number position and a seed.
float Random(float2 p, float seed)
{
    int2 cell = int2(floor(p));
    uint h = PcgHash(asuint(cell.x) ^ PcgHash(asuint(cell.y) ^ PcgHash((uint)max(seed, 0.0))));
    return (h & 0xFFFFFF) / 16777215.0;
}

#endif
