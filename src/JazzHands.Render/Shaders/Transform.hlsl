// Places a layer's linear picture in the frame: a textured quad whose corners are the cropped
// source rectangle carried through a 2x3 matrix from source pixels to target pixels. Scale,
// rotation, anchor, position, the media's fit and the preview quality are all folded into that
// one matrix on the CPU. Outside the quad the target stays transparent.
//
// A Normal layer with no masks is drawn straight onto the stack with premultiplied over blending
// (one, one minus source alpha), which is the Normal composite done by the output merger: only
// the pixels under the quad are touched, and no frame-sized target is cleared or read twice.

#include "Common.hlsli"

cbuffer TransformConstants : register(b0)
{
    float4 MatrixRow0;      // x' = dot(Row0.xyz, (x, y, 1))
    float4 MatrixRow1;      // y' = dot(Row1.xyz, (x, y, 1))
    float2 TargetSize;
    float2 SourceSize;      // the picture in its own logical pixels, which the matrix maps from
    float4 Crop;            // left, top, right, bottom of the source kept, as texture coordinates
    float2 TextureSize;     // the texture actually sampled, which bicubic filtering needs
    uint Bicubic;
    float Opacity;          // 1 into a target of its own; the layer's opacity when drawn straight onto the stack
};

Texture2D<float4> Source : register(t0);

struct QuadVertex
{
    float4 Position : SV_POSITION;
    float2 Uv : TEXCOORD0;
};

// Four vertices as a strip; the corner comes from the id.
QuadVertex VsMain(uint vertexId : SV_VertexID)
{
    float2 corner = float2(vertexId & 1, vertexId >> 1);
    float2 uv = lerp(Crop.xy, Crop.zw, corner);
    float3 source = float3(uv * SourceSize, 1.0);
    float2 target = float2(dot(MatrixRow0.xyz, source), dot(MatrixRow1.xyz, source));

    QuadVertex output;
    output.Position = float4(target.x / TargetSize.x * 2.0 - 1.0, 1.0 - target.y / TargetSize.y * 2.0, 0.0, 1.0);
    output.Uv = uv;
    return output;
}

float4 PsMain(QuadVertex input) : SV_TARGET
{
    float4 placed = Bicubic != 0
        ? SampleBicubic(Source, input.Uv, TextureSize)
        : Source.SampleLevel(LinearClamp, input.Uv, 0);

    return placed * Opacity;
}
