// Shared by every pass: the full screen triangle, the samplers, and the small helpers a pass
// would otherwise copy. Included with #include "Common.hlsli"; ShaderLibrary resolves it.

#ifndef JAZZ_COMMON_HLSLI
#define JAZZ_COMMON_HLSLI

SamplerState LinearClamp : register(s0);
SamplerState PointClamp : register(s1);
SamplerState LinearWrap : register(s2);

struct FullScreenVertex
{
    float4 Position : SV_POSITION;
    float2 Uv : TEXCOORD0;
};

// One oversized triangle covers the target with no vertex buffer and no seam on the diagonal.
FullScreenVertex FullScreenTriangle(uint vertexId)
{
    FullScreenVertex output;
    output.Uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(output.Uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return output;
}

float Luma709(float3 rgb)
{
    return dot(rgb, float3(0.2126, 0.7152, 0.0722));
}

float4 Premultiply(float4 straight)
{
    return float4(straight.rgb * straight.a, straight.a);
}

float4 Unpremultiply(float4 premultiplied)
{
    return premultiplied.a > 1e-6 ? float4(premultiplied.rgb / premultiplied.a, premultiplied.a) : float4(0.0, 0.0, 0.0, 0.0);
}

// A cheap hash for dithering and noise: the same pixel gives the same value every frame.
float Hash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// Catmull-Rom from nine bilinear taps rather than sixteen point taps, the usual trick: the
// weights of each pair of inner taps are folded into one filtered fetch between them.
float4 SampleBicubic(Texture2D<float4> source, float2 uv, float2 size)
{
    float2 position = uv * size;
    float2 centre = floor(position - 0.5) + 0.5;
    float2 f = position - centre;

    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);

    float2 w12 = w1 + w2;
    float2 offset12 = w2 / w12;

    float2 texel = 1.0 / size;
    float2 p0 = (centre - 1.0) * texel;
    float2 p3 = (centre + 2.0) * texel;
    float2 p12 = (centre + offset12) * texel;

    float4 result = 0.0;
    result += source.SampleLevel(LinearClamp, float2(p0.x, p0.y), 0) * w0.x * w0.y;
    result += source.SampleLevel(LinearClamp, float2(p12.x, p0.y), 0) * w12.x * w0.y;
    result += source.SampleLevel(LinearClamp, float2(p3.x, p0.y), 0) * w3.x * w0.y;

    result += source.SampleLevel(LinearClamp, float2(p0.x, p12.y), 0) * w0.x * w12.y;
    result += source.SampleLevel(LinearClamp, float2(p12.x, p12.y), 0) * w12.x * w12.y;
    result += source.SampleLevel(LinearClamp, float2(p3.x, p12.y), 0) * w3.x * w12.y;

    result += source.SampleLevel(LinearClamp, float2(p0.x, p3.y), 0) * w0.x * w3.y;
    result += source.SampleLevel(LinearClamp, float2(p12.x, p3.y), 0) * w12.x * w3.y;
    result += source.SampleLevel(LinearClamp, float2(p3.x, p3.y), 0) * w3.x * w3.y;

    // Catmull-Rom overshoots; a premultiplied pixel must not come out with colour above its alpha.
    result.a = saturate(result.a);
    result.rgb = clamp(result.rgb, 0.0, max(result.a, 0.0) * 65504.0);
    return result;
}

#endif
