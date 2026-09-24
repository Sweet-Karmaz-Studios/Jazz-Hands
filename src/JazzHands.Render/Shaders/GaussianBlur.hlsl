// video.blur.gaussian: a separable Gaussian, one direction per pass. Pairs of taps are folded into
// one bilinear fetch between them, which halves the fetches for the same kernel. A wide blur is
// run on a smaller copy (PsDown halves it, a 2x2 box) and drawn back up (PsUp), which keeps the
// cost bounded however large the radius; the result is already smooth, so nothing is lost.

#include "Effect.hlsli"

cbuffer BlurConstants : register(b1)
{
    float2 Direction;       // one texel along the blur axis, in texture coordinates
    float Sigma;            // standard deviation in texels of this pass's target
    int Pairs;              // folded tap pairs each side
    uint RepeatEdges;       // 1 to repeat the edge texels past the frame, 0 to blur in transparency
    float3 BlurPadding;
};

Texture2D<float4> Input : register(t0);

float Gaussian(float offset)
{
    return exp(-0.5 * offset * offset / (Sigma * Sigma));
}

float4 Tap(float2 uv)
{
    if (RepeatEdges == 0 && (any(uv < 0.0) || any(uv > 1.0)))
    {
        return 0.0;
    }

    return Input.SampleLevel(LinearClamp, uv, 0);
}

float4 PsBlur(FullScreenVertex input) : SV_TARGET
{
    if (Sigma <= 0.0)
    {
        return Input.Load(int3(input.Position.xy, 0));
    }

    float2 uv = input.Uv;
    float4 sum = Input.SampleLevel(PointClamp, uv, 0);
    float weight = 1.0;

    [loop]
    for (int pair = 0; pair < Pairs; pair++)
    {
        float near = 2.0 * pair + 1.0;
        float far = near + 1.0;
        float nearWeight = Gaussian(near);
        float farWeight = Gaussian(far);
        float both = nearWeight + farWeight;
        float offset = (near * nearWeight + far * farWeight) / both;

        sum += (Tap(uv + Direction * offset) + Tap(uv - Direction * offset)) * both;
        weight += 2.0 * both;
    }

    return sum / weight;
}

// Half size: each target texel lands between four source texels, so one bilinear fetch is their mean.
float4 PsDown(FullScreenVertex input) : SV_TARGET
{
    return Input.SampleLevel(LinearClamp, input.Uv, 0);
}

// Back up to size, filtered.
float4 PsUp(FullScreenVertex input) : SV_TARGET
{
    return Input.SampleLevel(LinearClamp, input.Uv, 0);
}
