// What every transition pass shares, the built-in ones and those written outside the build: the
// constants TransitionValues fills at b1, the outgoing picture at t0 and the incoming one at t1,
// and the helpers most transitions need. Both pictures are premultiplied linear light, frame
// sized at the working resolution; so is what a transition returns. Everything in Effect.hlsli is
// here too: TexelSize, Resolution, Time, QualityScale, ToPerceptual, Random and the rest.

#ifndef JAZZ_TRANSITION_HLSLI
#define JAZZ_TRANSITION_HLSLI

#include "Effect.hlsli"

cbuffer TransitionConstants : register(b1)
{
    float Progress;     // 0 all outgoing to 1 all incoming, eased
    float Seed;         // a whole number under 2^24, the same for the transition on every frame
    uint Mode;          // the transition's main choice, as the index of its value
    uint Flag;          // a switch
    float4 Values;      // per transition
    float4 More;        // per transition
    float4 Tint;        // a colour, premultiplied linear light
};

Texture2D<float4> Outgoing : register(t0);
Texture2D<float4> Incoming : register(t1);

float4 LoadOutgoing(float2 position)
{
    return Outgoing.Load(int3(position, 0));
}

float4 LoadIncoming(float2 position)
{
    return Incoming.Load(int3(position, 0));
}

// Off the frame is transparent rather than the edge stretched: a picture pushed away leaves
// nothing behind it.
bool OnFrame(float2 uv)
{
    return all(uv >= 0.0) && all(uv <= 1.0);
}

float4 SampleOutgoing(float2 uv)
{
    return OnFrame(uv) ? Outgoing.SampleLevel(LinearClamp, uv, 0) : 0.0;
}

float4 SampleIncoming(float2 uv)
{
    return OnFrame(uv) ? Incoming.SampleLevel(LinearClamp, uv, 0) : 0.0;
}

// A mix that looks even to the eye, as a cross dissolve does in every editor: in sRGB-encoded
// values. The same mix in linear light brightens the middle, which is the film dissolve.
float4 MixPerceptual(float4 a, float4 b, float t)
{
    float3 encodedA = LinearToSrgb(max(a.rgb, 0.0));
    float3 encodedB = LinearToSrgb(max(b.rgb, 0.0));
    return float4(SrgbToLinear(lerp(encodedA, encodedB, t)), lerp(a.a, b.a, t));
}

// How much of the incoming picture shows at a signed distance from a wipe's edge, in texels:
// positive is the incoming side. A softness of zero is a hard edge.
float Reveal(float distance, float softness)
{
    return softness > 1e-3 ? smoothstep(-softness, softness, distance) : step(0.0, distance);
}

// The two pictures either side of a wipe's edge, with an optional border of the tint along it.
float4 Wipe(float4 outgoing, float4 incoming, float distance, float softness, float border)
{
    float4 mixed = lerp(outgoing, incoming, Reveal(distance, softness));
    if (border > 1e-3)
    {
        float halfWidth = border * 0.5;
        float band = 1.0 - smoothstep(halfWidth, halfWidth + max(softness, 1.0), abs(distance));
        mixed = lerp(mixed, Tint, band);
    }

    return mixed;
}

#endif
