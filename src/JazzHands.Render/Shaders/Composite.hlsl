// Puts one layer on the stack: destination (t0) and layer (t1) in, the new destination out. Both
// are premultiplied linear light at the same size, so a pixel reads its own texel with Load.
//
// Separable blend modes follow the W3C compositing spec: the mode B works on straight colour and
// the result is weighted by the two alphas, so a mode never affects pixels where either side is
// transparent. Add is the one exception, a plain sum, because that is what a flare over black is
// expected to do. The matte (t2), when there is one, scales the layer's coverage.

#include "Common.hlsli"

#define BLEND_NORMAL 0
#define BLEND_ADD 1
#define BLEND_MULTIPLY 2
#define BLEND_SCREEN 3
#define BLEND_OVERLAY 4
#define BLEND_DARKEN 5
#define BLEND_LIGHTEN 6
#define BLEND_DIFFERENCE 7
#define BLEND_SOFT_LIGHT 8
#define BLEND_HARD_LIGHT 9

cbuffer CompositeConstants : register(b0)
{
    float Opacity;
    uint Mode;
    uint HasMatte;
    float Padding;
};

Texture2D<float4> Destination : register(t0);
Texture2D<float4> Layer : register(t1);
Texture2D<float4> Matte : register(t2);

FullScreenVertex VsMain(uint vertexId : SV_VertexID)
{
    return FullScreenTriangle(vertexId);
}

float3 HardLight(float3 cb, float3 cs)
{
    return cs <= 0.5 ? cb * 2.0 * cs : 1.0 - (1.0 - cb) * (1.0 - (2.0 * cs - 1.0));
}

float3 SoftLight(float3 cb, float3 cs)
{
    float3 d = cb <= 0.25 ? ((16.0 * cb - 12.0) * cb + 4.0) * cb : sqrt(cb);
    return cs <= 0.5 ? cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb) : cb + (2.0 * cs - 1.0) * (d - cb);
}

float3 Blend(float3 cb, float3 cs)
{
    // The contrast modes are defined on the unit range; linear light over 1 would make them
    // meaningless, so they see it clamped.
    float3 b = saturate(cb);
    float3 s = saturate(cs);

    switch (Mode)
    {
        case BLEND_MULTIPLY:
            return cb * cs;
        case BLEND_SCREEN:
            return b + s - b * s;
        case BLEND_OVERLAY:
            return HardLight(s, b);
        case BLEND_DARKEN:
            return min(cb, cs);
        case BLEND_LIGHTEN:
            return max(cb, cs);
        case BLEND_DIFFERENCE:
            return abs(cb - cs);
        case BLEND_SOFT_LIGHT:
            return SoftLight(b, s);
        case BLEND_HARD_LIGHT:
            return HardLight(b, s);
        default:
            return cs;
    }
}

float4 PsMain(FullScreenVertex input) : SV_TARGET
{
    int3 texel = int3(input.Position.xy, 0);
    float4 destination = Destination.Load(texel);
    float4 layer = Layer.Load(texel) * Opacity;

    if (HasMatte != 0)
    {
        layer *= Matte.Load(texel).a;
    }

    if (Mode == BLEND_NORMAL)
    {
        return layer + destination * (1.0 - layer.a);
    }

    if (Mode == BLEND_ADD)
    {
        return float4(layer.rgb + destination.rgb, saturate(layer.a + destination.a));
    }

    float sa = layer.a;
    float da = destination.a;
    float3 cs = sa > 1e-6 ? layer.rgb / sa : 0.0;
    float3 cb = da > 1e-6 ? destination.rgb / da : 0.0;

    float3 rgb = (1.0 - da) * layer.rgb + (1.0 - sa) * destination.rgb + sa * da * Blend(cb, cs);
    return float4(rgb, sa + da - sa * da);
}
