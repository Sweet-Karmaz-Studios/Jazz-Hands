// A frame round a layer's picture: rounded corners, a border and a soft shadow, worked out from
// where the picture is (its placement), so they follow it as it moves and scales. Distances are
// signed distance fields in texels, which keeps every edge smooth.

#include "Effect.hlsli"

cbuffer FrameConstants : register(b1)
{
    float4 Inverse;     // the 2 by 2 of the matrix from texels to picture pixels: m11 m12 m21 m22
    float4 Offset;      // xy its translation; z texels per picture pixel; w the corner radius in texels
    float4 Rect;        // the picture kept, in picture pixels: left, top, right, bottom
    float4 Border;      // the border's colour, premultiplied linear
    float4 Shadow;      // the shadow's colour, premultiplied linear, its opacity included
    float4 Sizes;       // x the border width, yz the shadow's offset, w its softness, all in texels
};

Texture2D<float4> Input : register(t0);

// Distance in texels from the picture's rounded rectangle at a texel: negative inside.
float Distance(float2 texel)
{
    float2 q = float2(dot(texel, Inverse.xz), dot(texel, Inverse.yw)) + Offset.xy;
    float2 centre = (Rect.xy + Rect.zw) * 0.5;
    float2 extent = (Rect.zw - Rect.xy) * 0.5;
    float radius = min(Offset.w / max(Offset.z, 1e-6), min(extent.x, extent.y));
    float2 d = abs(q - centre) - (extent - radius);
    return (length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - radius) * Offset.z;
}

float4 PsFrame(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    float d = Distance(p);

    // The picture with its corners rounded off.
    float inside = saturate(0.5 - d);
    float4 result = Input.Load(int3(p, 0)) * inside;

    // The border, inside the edge.
    if (Sizes.x > 0.0)
    {
        float band = inside - saturate(0.5 - (d + Sizes.x));
        result = (Border * band) + (result * (1.0 - (Border.a * band)));
    }

    // The shadow, under everything, where the picture would be moved by the offset and blurred.
    if (Shadow.a > 0.0)
    {
        float s = Distance(p - Sizes.yz);
        float soft = max(Sizes.w, 1.0);
        float cover = 1.0 - smoothstep(-soft * 0.5, soft * 0.5, s);
        result += Shadow * cover * (1.0 - result.a);
    }

    return result;
}
