// video.transform: moves, scales and turns a picture that has already been placed in the frame,
// the way Premiere's Transform effect does next to a clip's own Motion. The input is drawn as a
// quad through a 2x3 matrix, frame texels to frame texels, onto a cleared target.

#include "Effect.hlsli"

cbuffer TransformEffectConstants : register(b1)
{
    float4 MatrixRow0;      // x' = dot(Row0.xyz, (x, y, 1)), in target texels
    float4 MatrixRow1;      // y' = dot(Row1.xyz, (x, y, 1))
    float Opacity;
    uint Bicubic;
    float2 TransformPadding;
};

Texture2D<float4> Input : register(t0);

struct QuadVertex
{
    float4 Position : SV_POSITION;
    float2 Uv : TEXCOORD0;
};

QuadVertex VsQuad(uint vertexId : SV_VertexID)
{
    float2 corner = float2(vertexId & 1, vertexId >> 1);
    float3 source = float3(corner * Resolution, 1.0);
    float2 target = float2(dot(MatrixRow0.xyz, source), dot(MatrixRow1.xyz, source));

    QuadVertex output;
    output.Position = float4(target.x / Resolution.x * 2.0 - 1.0, 1.0 - target.y / Resolution.y * 2.0, 0.0, 1.0);
    output.Uv = corner;
    return output;
}

float4 PsQuad(QuadVertex input) : SV_TARGET
{
    float4 placed = Bicubic != 0
        ? SampleBicubic(Input, input.Uv, Resolution)
        : Input.SampleLevel(LinearClamp, input.Uv, 0);

    return placed * Opacity;
}
