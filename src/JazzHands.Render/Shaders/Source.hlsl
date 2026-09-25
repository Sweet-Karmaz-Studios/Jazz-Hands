// The first pass of every layer: a decoded frame, in whatever layout it arrived, to premultiplied
// linear BT.709 light at half float, at the source's own size. Everything after this works in
// that one format, so range expansion, the matrix and the transfer function happen here and
// nowhere else. See the color-science skill.

#include "Common.hlsli"
#include "Color.hlsli"

// Layouts, matching SourceLayout in C#.
#define LAYOUT_SEMIPLANAR 0   // NV12, P010: luma, then interleaved chroma
#define LAYOUT_PLANAR 1       // yuv420p and friends: Y, U, V
#define LAYOUT_RGBA 2         // one interleaved RGBA plane, straight alpha
#define LAYOUT_GBRA_PLANAR 3  // four float planes in G, B, R, A order
#define LAYOUT_PLANAR_ALPHA 4 // yuva: Y, U, V and a plane of straight alpha

cbuffer SourceConstants : register(b0)
{
    float3 MatrixRow0;
    float SampleScale;      // turns a UNORM sample back into the fraction of full scale it means
    float3 MatrixRow1;
    float LumaOffset;
    float3 MatrixRow2;
    float ChromaOffset;
    float LumaRange;
    float ChromaRange;
    uint Transfer;
    uint Layout;
    uint Primaries;         // PRIMARIES_*: BT.2020 is brought into BT.709 here
    uint ToneOperator;      // TONEMAP_*, for PQ and HLG
    float PeakNits;         // the source's peak, which the tone map brings down to SDR white
    float Desaturate;       // how much compressed highlights lose their colour
};

float3 Decode(float3 encoded)
{
    return ToLinear(encoded, Transfer, Primaries, ToneOperator, PeakNits, Desaturate);
}

Texture2D<float4> Plane0 : register(t0);
Texture2D<float4> Plane1 : register(t1);
Texture2D<float4> Plane2 : register(t2);
Texture2D<float4> Plane3 : register(t3);

FullScreenVertex VsMain(uint vertexId : SV_VertexID)
{
    return FullScreenTriangle(vertexId);
}

float4 PsMain(FullScreenVertex input) : SV_TARGET
{
    float2 uv = input.Uv;

    if (Layout == LAYOUT_RGBA)
    {
        float4 straight = Plane0.SampleLevel(LinearClamp, uv, 0);
        return Premultiply(float4(Decode(straight.rgb), straight.a));
    }

    if (Layout == LAYOUT_GBRA_PLANAR)
    {
        float g = Plane0.SampleLevel(LinearClamp, uv, 0).r;
        float b = Plane1.SampleLevel(LinearClamp, uv, 0).r;
        float r = Plane2.SampleLevel(LinearClamp, uv, 0).r;
        float a = Plane3.SampleLevel(LinearClamp, uv, 0).r;
        return Premultiply(float4(Decode(float3(r, g, b)), saturate(a)));
    }

    float luma = Plane0.SampleLevel(LinearClamp, uv, 0).r * SampleScale;
    float4 first = Plane1.SampleLevel(LinearClamp, uv, 0);
    float2 chroma = Layout == LAYOUT_PLANAR
        ? float2(first.r, Plane2.SampleLevel(LinearClamp, uv, 0).r)
        : first.rg;
    chroma *= SampleScale;

    // Studio swing to full swing before the matrix, or black is not black.
    float3 yuv;
    yuv.x = (luma - LumaOffset) * LumaRange;
    yuv.y = (chroma.x - ChromaOffset) * ChromaRange;
    yuv.z = (chroma.y - ChromaOffset) * ChromaRange;

    float3 encoded = saturate(float3(dot(MatrixRow0, yuv), dot(MatrixRow1, yuv), dot(MatrixRow2, yuv)));
    if (Layout == LAYOUT_PLANAR_ALPHA)
    {
        // Alpha is full range whatever the colour's range, and straight: premultiplied here.
        float alpha = saturate(Plane3.SampleLevel(LinearClamp, uv, 0).r * SampleScale);
        return Premultiply(float4(Decode(encoded), alpha));
    }

    return float4(Decode(encoded), 1.0);
}
