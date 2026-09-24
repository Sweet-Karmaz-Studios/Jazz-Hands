// What every effect pass shares: Common.hlsli, the constants EffectContext.Draw binds at b0, and
// the full-screen triangle. An effect's own constants go in a cbuffer at b1 and its inputs from t0.
// Inputs and outputs are premultiplied linear light, frame sized at the working resolution.
// Pixel-sized parameters are sequence pixels; multiply by QualityScale for texels, so the preview at
// half size matches the export.

#ifndef JAZZ_EFFECT_HLSLI
#define JAZZ_EFFECT_HLSLI

#include "Common.hlsli"

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

#endif
