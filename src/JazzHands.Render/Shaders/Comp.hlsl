// The comp graph's own joins (Phase 49): a picture kept where a matte says. Merges are the
// compositor's own composite pass; everything else is an ordinary effect, generator or the 3D
// scene. The constants are EffectValues' layout: two float4s and four flags.

#include "Effect.hlsli"

cbuffer CompConstants : register(b1)
{
    float4 Values;      // unused
    float4 More;        // unused
    uint4 Flags;        // x: 0 alpha, 1 luma, 2 alpha inverted, 3 luma inverted
};

Texture2D<float4> Input : register(t0);
Texture2D<float4> MatteInput : register(t1);

// t0 kept where t1's alpha (or its brightness, unpremultiplied) is, or where it is not.
float4 PsMatte(FullScreenVertex input) : SV_TARGET
{
    int3 texel = int3(input.Position.xy, 0);
    float4 matte = MatteInput.Load(texel);
    float coverage = (Flags.x & 1) != 0
        ? saturate(dot(matte.a > 1e-6 ? matte.rgb / matte.a : 0.0, float3(0.2126, 0.7152, 0.0722))) * saturate(matte.a)
        : saturate(matte.a);
    if (Flags.x >= 2)
    {
        coverage = 1.0 - coverage;
    }

    return Input.Load(texel) * coverage;
}
