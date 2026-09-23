// Copies a display target, normally the program texture the compositor's output pass wrote,
// into a preview surface at a size and position. The quad vertex shader places a rectangle of the
// source onto a rectangle of the target, so fitting, letterboxing and zoom are all a matter of
// two rectangles.

cbuffer QuadConstants : register(b0)
{
    float4 DestRect;        // left, top, right, bottom as fractions of the target
    float4 SourceRect;      // left, top, right, bottom as texture coordinates
};

Texture2D<float4> Source : register(t0);
SamplerState LinearSampler : register(s0);

struct VertexOutput
{
    float4 Position : SV_POSITION;
    float2 Uv : TEXCOORD0;
};

// Four vertices as a strip, no buffers: the corner comes from the vertex id.
VertexOutput VsQuad(uint vertexId : SV_VertexID)
{
    float2 corner = float2(vertexId & 1, vertexId >> 1);
    float2 target = lerp(DestRect.xy, DestRect.zw, corner);

    VertexOutput output;
    output.Position = float4(target.x * 2.0 - 1.0, 1.0 - target.y * 2.0, 0.0, 1.0);
    output.Uv = lerp(SourceRect.xy, SourceRect.zw, corner);
    return output;
}

float4 PsBlit(VertexOutput input) : SV_TARGET
{
    return float4(Source.Sample(LinearSampler, input.Uv).rgb, 1.0);
}
