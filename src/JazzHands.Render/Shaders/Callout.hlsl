// Overlays drawn over a layer's own picture: the callout, a rounded inset showing a region of the
// picture at a zoom, with a ring round the region and a line from one to the other. Distances are
// in texels; shapes are signed distance fields, so every edge is smooth at any size.

#include "Effect.hlsli"

cbuffer CalloutConstants : register(b1)
{
    float4 Values;      // xy the region's centre, zw the inset's centre, in texels from the top left
    float4 More;        // x half the region's side, y the zoom, z the corner radius, w the border width
    uint4 Flags;        // x draw the line, y draw the ring round the region
    float4 Colour;      // the border, ring and line, premultiplied linear
};

Texture2D<float4> Input : register(t0);

// Distance from a rounded square: negative inside.
float RoundedBox(float2 p, float2 centre, float extent, float radius)
{
    radius = min(radius, extent);
    float2 q = abs(p - centre) - (extent - radius);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

// Distance from a line segment.
float Segment(float2 p, float2 a, float2 b)
{
    float2 ab = b - a;
    float t = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-6));
    return length(p - a - (ab * t));
}

float4 Over(float4 top, float4 bottom)
{
    return top + (bottom * (1.0 - top.a));
}

float4 PsCallout(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    float2 region = Values.xy;
    float2 inset = Values.zw;
    float radius = More.x;
    float zoom = max(More.y, 1e-3);
    float corner = More.z;
    float border = More.w;

    float4 result = Input.Load(int3(p, 0));
    float insetDistance = RoundedBox(p, inset, radius * zoom, corner);
    float regionDistance = RoundedBox(p, region, radius, corner / zoom);
    float stroke = max(border * 0.5, 0.75);

    if (Flags.x == 1 && border > 0.0)
    {
        float along = Segment(p, region, inset) - stroke * 0.5;
        float outside = saturate(0.5 + min(insetDistance, regionDistance + stroke));
        result = Over(Colour * saturate(0.5 - along) * outside, result);
    }

    if (Flags.y == 1 && border > 0.0)
    {
        float ring = abs(regionDistance + stroke * 0.5) - stroke * 0.5;
        result = Over(Colour * saturate(0.5 - ring), result);
    }

    // The zoomed region, then the border over its edge.
    float cover = saturate(0.5 - insetDistance);
    if (cover > 0.0)
    {
        float2 uv = (region + ((p - inset) / zoom)) * TexelSize;
        float4 zoomed = Input.SampleLevel(LinearClamp, uv, 0);
        result = lerp(result, zoomed, cover);
    }

    if (border > 0.0)
    {
        float edge = abs(insetDistance + border * 0.5) - border * 0.5;
        result = Over(Colour * saturate(0.5 - edge), result);
    }

    return result;
}
