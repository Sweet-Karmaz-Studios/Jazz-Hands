// Optical flow retiming (Phase 42): the picture between two source frames made by moving each
// towards the moment along the motion between them, rather than crossfading them.
//
// Flow is found at half the frame's size on a luma pyramid, coarse to fine, by iterative
// Lucas-Kanade in a Gaussian window, in both directions at once: a flow texture holds the
// forward flow (first frame to second) in xy and the backward flow in zw, in texels of its level.
// Each iteration is followed by a median, which throws out the odd wrong vector. The in-between
// frame reads both frames along the flows to the moment (Super SloMo's approximation, exact for
// steady motion) and weights each by how far the moment is from it and by how well its flow
// agrees with the flow back, so what one frame hides is taken from the other.

#include "Effect.hlsli"

cbuffer FlowConstants : register(b1)
{
    float Progress;     // 0 the first frame, 1 the second
    int Radius;         // the Lucas-Kanade window's reach, in texels
    float Lambda;       // regularisation: keeps flat areas still rather than wild
    float MaxStep;      // the most one iteration may move a vector, in texels
    float FlowScale;    // flow texels to output texels (the flow is at half size)
    float3 FlowPadding;
};

Texture2D FirstFrame : register(t0);
Texture2D SecondFrame : register(t1);
Texture2D Flow : register(t2);

// Lumas of the two frames, in a perceptual encoding so dark detail is followed as well as light.
float Luma(float4 premultiplied)
{
    float l = dot(max(premultiplied.rgb, 0.0), float3(0.2126, 0.7152, 0.0722));
    return pow(min(l, 16.0), 1.0 / 2.2);
}

// t0 the first frame and t1 the second at full size; the output is half size, so a bilinear
// read at its texel centre averages the four texels under it.
float4 PsLuma(FullScreenVertex input) : SV_TARGET
{
    float2 uv = input.Position.xy * TexelSize;
    return float4(Luma(FirstFrame.SampleLevel(LinearClamp, uv, 0)), Luma(SecondFrame.SampleLevel(LinearClamp, uv, 0)), 0, 0);
}

// t0 a pyramid level; the output is the next one down, half its size.
float4 PsDown(FullScreenVertex input) : SV_TARGET
{
    return FirstFrame.SampleLevel(LinearClamp, input.Position.xy * TexelSize, 0);
}

// t2 the coarser level's flow; the output is this level's starting flow, twice as long.
float4 PsUpsample(FullScreenVertex input) : SV_TARGET
{
    return Flow.SampleLevel(LinearClamp, input.Position.xy * TexelSize, 0) * 2.0;
}

// One Lucas-Kanade step each way. t0 this level's lumas (first in r, second in g), t2 the flow.
float4 PsLucasKanade(FullScreenVertex input) : SV_TARGET
{
    int2 size = int2(Resolution);
    int2 p = int2(input.Position.xy);
    float4 flow = Flow.Load(int3(p, 0));
    float2 forward = flow.xy;
    float2 backward = flow.zw;

    float3 gf = 0;   // forward: sum w Ix^2, w IxIy, w Iy^2 of the first frame
    float2 bf = 0;   // forward: sum w Ix It, w Iy It
    float3 gb = 0;   // backward: the same of the second frame
    float2 bb = 0;
    float sigma2 = max(1.0, Radius * Radius * 0.5);

    for (int dy = -Radius; dy <= Radius; dy++)
    {
        for (int dx = -Radius; dx <= Radius; dx++)
        {
            int2 q = clamp(p + int2(dx, dy), int2(0, 0), size - 1);
            float2 here = FirstFrame.Load(int3(q, 0)).rg;
            float2 left = FirstFrame.Load(int3(clamp(q - int2(1, 0), int2(0, 0), size - 1), 0)).rg;
            float2 right = FirstFrame.Load(int3(clamp(q + int2(1, 0), int2(0, 0), size - 1), 0)).rg;
            float2 up = FirstFrame.Load(int3(clamp(q - int2(0, 1), int2(0, 0), size - 1), 0)).rg;
            float2 down = FirstFrame.Load(int3(clamp(q + int2(0, 1), int2(0, 0), size - 1), 0)).rg;
            float2 ix = (right - left) * 0.5;
            float2 iy = (down - up) * 0.5;
            float w = exp(-(dx * dx + dy * dy) / (2.0 * sigma2));

            float2 at = (float2(q) + 0.5) * TexelSize;
            float itf = FirstFrame.SampleLevel(LinearClamp, at + forward * TexelSize, 0).g - here.r;
            float itb = FirstFrame.SampleLevel(LinearClamp, at + backward * TexelSize, 0).r - here.g;

            gf += w * float3(ix.r * ix.r, ix.r * iy.r, iy.r * iy.r);
            bf += w * itf * float2(ix.r, iy.r);
            gb += w * float3(ix.g * ix.g, ix.g * iy.g, iy.g * iy.g);
            bb += w * itb * float2(ix.g, iy.g);
        }
    }

    float2 stepF = 0;
    float2 stepB = 0;
    {
        float a = gf.x + Lambda, b = gf.y, d = gf.z + Lambda;
        float det = a * d - b * b;
        stepF = det > 1e-12 ? -float2(d * bf.x - b * bf.y, a * bf.y - b * bf.x) / det : 0;
    }
    {
        float a = gb.x + Lambda, b = gb.y, d = gb.z + Lambda;
        float det = a * d - b * b;
        stepB = det > 1e-12 ? -float2(d * bb.x - b * bb.y, a * bb.y - b * bb.x) / det : 0;
    }

    float lf = length(stepF);
    float lb = length(stepB);
    stepF *= lf > MaxStep ? MaxStep / lf : 1.0;
    stepB *= lb > MaxStep ? MaxStep / lb : 1.0;
    return float4(forward + stepF, backward + stepB);
}

// The median of nine, by a sorting network: robust against a lone wrong vector.
float Median9(float v[9])
{
    [unroll] for (int i = 0; i < 9; i++)
    {
        [unroll] for (int j = i + 1; j < 9; j++)
        {
            float lo = min(v[i], v[j]);
            float hi = max(v[i], v[j]);
            v[i] = lo;
            v[j] = hi;
        }
    }

    return v[4];
}

// t2 the flow; each component the median of its three by three neighbourhood.
float4 PsMedian(FullScreenVertex input) : SV_TARGET
{
    int2 size = int2(Resolution);
    int2 p = int2(input.Position.xy);
    float4 samples[9];
    [unroll] for (int k = 0; k < 9; k++)
    {
        samples[k] = Flow.Load(int3(clamp(p + int2(k % 3 - 1, k / 3 - 1), int2(0, 0), size - 1), 0));
    }

    float4 result;
    [unroll] for (int c = 0; c < 4; c++)
    {
        float v[9];
        [unroll] for (int k2 = 0; k2 < 9; k2++)
        {
            v[k2] = samples[k2][c];
        }

        result[c] = Median9(v);
    }

    return result;
}

// The in-between frame at Progress. t0 the first frame, t1 the second, t2 the finest flow.
float4 PsInterpolate(FullScreenVertex input) : SV_TARGET
{
    float t = Progress;
    float2 uv = input.Position.xy * TexelSize;
    float4 flow = Flow.SampleLevel(LinearClamp, uv, 0) * FlowScale;
    float2 f01 = flow.xy;
    float2 f10 = flow.zw;

    // Where the moment's pixel was in each frame.
    float2 toFirst = -(1.0 - t) * t * f01 + t * t * f10;
    float2 toSecond = (1.0 - t) * (1.0 - t) * f01 - t * (1.0 - t) * f10;
    float2 atFirst = uv + toFirst * TexelSize;
    float2 atSecond = uv + toSecond * TexelSize;
    float4 a = FirstFrame.SampleLevel(LinearClamp, atFirst, 0);
    float4 b = SecondFrame.SampleLevel(LinearClamp, atSecond, 0);

    // How well each frame's flow comes back to where it started: where it does not, that frame's
    // pixel is hidden in the other, and the moment leans on the frame that shows it.
    float2 there = Flow.SampleLevel(LinearClamp, atFirst, 0).xy * FlowScale;
    float2 back = Flow.SampleLevel(LinearClamp, atFirst + there * TexelSize, 0).zw * FlowScale;
    float errorFirst = length(there + back);
    float2 thereB = Flow.SampleLevel(LinearClamp, atSecond, 0).zw * FlowScale;
    float2 backB = Flow.SampleLevel(LinearClamp, atSecond + thereB * TexelSize, 0).xy * FlowScale;
    float errorSecond = length(thereB + backB);

    float wa = (1.0 - t) * (exp(-errorFirst * errorFirst * 0.25) + 0.02);
    float wb = t * (exp(-errorSecond * errorSecond * 0.25) + 0.02);
    return (a * wa + b * wb) / max(wa + wb, 1e-6);
}
