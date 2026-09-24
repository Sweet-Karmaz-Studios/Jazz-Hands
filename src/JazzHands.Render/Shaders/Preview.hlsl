// Copies a display target, normally the program texture the compositor's output pass wrote,
// into a preview surface at a size and position. The quad vertex shader places a rectangle of the
// source onto a rectangle of the target, so fitting, letterboxing and zoom are all a matter of
// two rectangles.

cbuffer QuadConstants : register(b0)
{
    float4 DestRect;        // left, top, right, bottom as fractions of the target
    float4 SourceRect;      // left, top, right, bottom as texture coordinates
    uint Display;           // 0 sRGB monitor, 1 gamma 2.2, 2 BT.1886: what the screen expects
    float3 Padding;
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

// The program is BT.1886, the signal as delivered. For a monitor that expects something else it
// is decoded to light as a reference display would show it and encoded for that monitor, so the
// picture on a desktop looks as it will on a television.
float4 PsBlit(VertexOutput input) : SV_TARGET
{
    float3 signal = Source.Sample(LinearSampler, input.Uv).rgb;
    if (Display == 2)
    {
        return float4(signal, 1.0);
    }

    float3 light = pow(max(signal, 0.0), 2.4);
    float3 shown = Display == 1
        ? pow(light, 1.0 / 2.2)
        : (light <= 0.0031308 ? light * 12.92 : 1.055 * pow(light, 1.0 / 2.4) - 0.055);
    return float4(shown, 1.0);
}
