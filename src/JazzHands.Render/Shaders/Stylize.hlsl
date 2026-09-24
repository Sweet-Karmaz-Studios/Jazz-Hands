// The single pass stylize effects: invert, posterize, pixelate, vignette, noise, flicker and find
// edges. One file, one pixel shader each, one constant buffer shaped for all of them: the effect
// classes fill the members they use. Colour work happens in a perceptual (sRGB) encoding, which is
// what the parameters mean to a person.

#include "Effect.hlsli"

cbuffer StylizeConstants : register(b1)
{
    float4 Values;      // per effect, see each shader
    float4 More;        // per effect
    uint4 Flags;        // per effect
};

Texture2D<float4> Input : register(t0);

float4 Load(float2 position)
{
    return Input.Load(int3(position, 0));
}

// Values.x amount 0..1; Flags.x 0 colour, 1 luma (keeps hue, turns light dark).
float4 PsInvert(FullScreenVertex input) : SV_TARGET
{
    float4 c = ToPerceptual(Load(input.Position.xy));
    float3 inverted = Flags.x == 1 ? c.rgb + (1.0 - 2.0 * Luma709(c.rgb)) : 1.0 - c.rgb;
    c.rgb = lerp(c.rgb, saturate(inverted), Values.x);
    return FromPerceptual(c);
}

// Values.x levels per channel, 2 or more.
float4 PsPosterize(FullScreenVertex input) : SV_TARGET
{
    float4 c = ToPerceptual(Load(input.Position.xy));
    float steps = max(Values.x - 1.0, 1.0);
    c.rgb = floor(c.rgb * steps + 0.5) / steps;
    return FromPerceptual(c);
}

// Values.x cell size in texels. Cells are laid out from the frame centre, so a cell always sits
// on it however the size is animated.
float4 PsPixelate(FullScreenVertex input) : SV_TARGET
{
    float size = max(Values.x, 1.0);
    float2 centre = Resolution * 0.5;
    float2 cell = (floor((input.Position.xy - centre) / size) + 0.5) * size + centre;

    // Snapped to a texel centre: a cell centre on a texel boundary is read from one neighbour by
    // one device and the other by another.
    cell = floor(cell) + 0.5;
    return Input.SampleLevel(PointClamp, clamp(cell, 0.5, Resolution - 0.5) * TexelSize, 0);
}

// Values: x amount (-1 lightens, 1 darkens), y size (radius where it starts, 1 is the corner),
// z softness, w roundness (1 a circle, 0 the frame's own shape).
float4 PsVignette(FullScreenVertex input) : SV_TARGET
{
    float4 c = Load(input.Position.xy);
    float aspect = Resolution.x / Resolution.y;
    float2 stretch = float2(lerp(1.0, aspect, Values.w), 1.0);
    float2 d = (input.Uv - 0.5) * stretch;
    float r = length(d) / length(0.5 * stretch);
    float v = smoothstep(Values.y, Values.y + max(Values.z, 1e-3), r);

    if (Values.x >= 0.0)
    {
        return float4(c.rgb * (1.0 - Values.x * v), c.a);
    }

    return float4(lerp(c.rgb, c.aaa, -Values.x * v), c.a);
}

// Values: x amount, y grain size in texels, z seed. Flags.x 1 for grey grain, 0 for colour.
float4 PsNoise(FullScreenVertex input) : SV_TARGET
{
    float4 c = ToPerceptual(Load(input.Position.xy));
    float2 cell = floor(input.Position.xy / max(Values.y, 1.0));

    // Three hashes summed are close enough to a bell curve for grain.
    float3 n;
    n.r = (Random(cell, Values.z) + Random(cell + 17.0, Values.z) + Random(cell + 43.0, Values.z)) / 3.0;
    n.g = Flags.x == 1 ? n.r : (Random(cell, Values.z + 1.0) + Random(cell + 19.0, Values.z + 1.0) + Random(cell + 47.0, Values.z + 1.0)) / 3.0;
    n.b = Flags.x == 1 ? n.r : (Random(cell, Values.z + 2.0) + Random(cell + 23.0, Values.z + 2.0) + Random(cell + 53.0, Values.z + 2.0)) / 3.0;

    c.rgb += (n - 0.5) * 2.0 * Values.x;
    return FromPerceptual(c);
}

// Values.x the brightness multiplier for this frame, worked out on the CPU from the time.
float4 PsFlicker(FullScreenVertex input) : SV_TARGET
{
    float4 c = Load(input.Position.xy);
    return float4(c.rgb * Values.x, c.a);
}

float EdgeLuma(float2 position)
{
    return Luma709(ToPerceptual(Load(clamp(position, 0.0, Resolution - 1.0))).rgb);
}

// Values: x strength, y mix (1 edges only, 0 the picture). Flags.x 1 for coloured edges;
// Flags.y 1 to invert (dark lines on white, like a drawing).
float4 PsEdges(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    float tl = EdgeLuma(p + float2(-1, -1));
    float t = EdgeLuma(p + float2(0, -1));
    float tr = EdgeLuma(p + float2(1, -1));
    float l = EdgeLuma(p + float2(-1, 0));
    float r = EdgeLuma(p + float2(1, 0));
    float bl = EdgeLuma(p + float2(-1, 1));
    float b = EdgeLuma(p + float2(0, 1));
    float br = EdgeLuma(p + float2(1, 1));

    float gx = (tr + 2.0 * r + br) - (tl + 2.0 * l + bl);
    float gy = (bl + 2.0 * b + br) - (tl + 2.0 * t + tr);
    float edge = saturate(length(float2(gx, gy)) * Values.x);

    float4 c = ToPerceptual(Load(p));
    float3 lines = Flags.x == 1 ? c.rgb * edge : edge.xxx;
    if (Flags.y == 1)
    {
        lines = Flags.x == 1 ? 1.0 - edge.xxx * (1.0 - c.rgb) : 1.0 - edge.xxx;
    }

    c.rgb = lerp(c.rgb, lines, Values.y);
    return FromPerceptual(c);
}
