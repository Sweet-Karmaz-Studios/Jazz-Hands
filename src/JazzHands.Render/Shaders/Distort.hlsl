// The single pass distortions: mirror, tile, lens distortion, Ken Burns and crop. Each moves where
// a pixel reads from rather than what colour it is; anything read from outside the picture is
// transparent unless the effect says to repeat the edge.

#include "Effect.hlsli"

cbuffer DistortConstants : register(b1)
{
    float4 Values;      // per effect, see each shader
    float4 More;        // per effect
    uint4 Flags;        // per effect
};

Texture2D<float4> Input : register(t0);

float4 Fetch(float2 uv, bool repeatEdges)
{
    if (!repeatEdges && (any(uv < 0.0) || any(uv > 1.0)))
    {
        return 0.0;
    }

    return Input.SampleLevel(LinearClamp, uv, 0);
}

// Flags.x 0 left onto right, 1 right onto left, 2 top onto bottom, 3 bottom onto top, 4 the top
// left quarter onto all four. Values.xy the line's offset from the centre, in texels.
float4 PsMirror(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    float2 line_ = Resolution * 0.5 + Values.xy;

    bool x = Flags.x == 0 || Flags.x == 4;
    bool y = Flags.x == 2 || Flags.x == 4;
    if (x && p.x > line_.x) p.x = 2.0 * line_.x - p.x;
    if (Flags.x == 1 && p.x < line_.x) p.x = 2.0 * line_.x - p.x;
    if (y && p.y > line_.y) p.y = 2.0 * line_.y - p.y;
    if (Flags.x == 3 && p.y < line_.y) p.y = 2.0 * line_.y - p.y;

    return Fetch(p * TexelSize, false);
}

// Values.xy columns and rows. Flags.x 1 to mirror alternate tiles so the seams meet.
float4 PsTile(FullScreenVertex input) : SV_TARGET
{
    float2 t = input.Uv * max(Values.xy, 1.0);
    float2 f = frac(t);
    if (Flags.x == 1)
    {
        float2 cell = floor(t);
        f = lerp(f, 1.0 - f, fmod(cell, 2.0));
    }

    return Input.SampleLevel(LinearClamp, f, 0);
}

// Values: x amount (barrel above 0, pincushion below), y zoom, zw centre as a texture
// coordinate. Flags.x 1 to repeat the edge where the picture runs out.
float4 PsLens(FullScreenVertex input) : SV_TARGET
{
    float aspect = Resolution.x / Resolution.y;
    float2 centre = Values.zw;
    float2 d = (input.Uv - centre) * float2(aspect, 1.0);
    float r2 = dot(d, d) / dot(float2(aspect, 1.0) * 0.5, float2(aspect, 1.0) * 0.5);
    float factor = (1.0 + Values.x * r2) / max(Values.y, 1e-3);
    return Fetch(centre + (input.Uv - centre) * factor, Flags.x == 1);
}

// Values: x scale, zw the source point that sits at the frame centre as a texture coordinate.
float4 PsKenBurns(FullScreenVertex input) : SV_TARGET
{
    float2 uv = Values.zw + (input.Uv - 0.5) / max(Values.x, 1e-3);
    if (any(uv < 0.0) || any(uv > 1.0))
    {
        return 0.0;
    }

    return SampleBicubic(Input, uv, Resolution);
}

// Values: left, top, right, bottom kept edges as texture coordinates. More.x the feather in
// texels, inwards from each edge.
float4 PsCrop(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    float4 edges = Values * Resolution.xyxy;
    float inside = min(min(p.x - edges.x, p.y - edges.y), min(edges.z - p.x, edges.w - p.y));
    float keep = More.x > 0.0 ? saturate(inside / More.x) : step(0.0, inside);
    return Input.Load(int3(p, 0)) * keep;
}
