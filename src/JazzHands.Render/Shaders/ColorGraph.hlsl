// The joins of a colour graph (Phase 44): parallel nodes mixed, and a node limited by a
// qualifier's key. The nodes themselves are the ordinary colour effects. The constants are
// EffectValues' layout: two float4s and four flags.

#include "Effect.hlsli"

cbuffer ColorGraphConstants : register(b1)
{
    float4 Values;      // PsMix: the shares of t0 to t3
    float4 More;        // unused
    uint4 Flags;        // unused
};

Texture2D<float4> Input : register(t0);
Texture2D<float4> Second : register(t1);
Texture2D<float4> Third : register(t2);
Texture2D<float4> Fourth : register(t3);

// A parallel mixer: each input's share of it, in premultiplied linear light. The shares add up to
// one, and an input slot not in use has a share of nothing.
float4 PsMix(FullScreenVertex input) : SV_TARGET
{
    int3 texel = int3(input.Position.xy, 0);
    return Input.Load(texel) * Values.x + Second.Load(texel) * Values.y
        + Third.Load(texel) * Values.z + Fourth.Load(texel) * Values.w;
}

// A keyed node: t0 its input, t1 its correction, t2 the qualifier drawn as its matte (a grey the
// HSL qualifier encodes for viewing, so it is decoded back to the key here). The correction shows
// where the key is white, the input where it is black.
float4 PsKey(FullScreenVertex input) : SV_TARGET
{
    int3 texel = int3(input.Position.xy, 0);
    float key = saturate(LinearToSrgb(Third.Load(texel).rrr).r);
    return lerp(Input.Load(texel), Second.Load(texel), key);
}
