// The impact kit: shake and zoom punch (one move of the picture), flash, chromatic aberration and
// the impact frame. What changes over time is worked out on the CPU from the time and a seed, so
// these shaders only draw one moment.

#include "Effect.hlsli"

cbuffer ImpactConstants : register(b1)
{
    float4 Values;      // per effect, see each shader
    float4 More;        // per effect
    uint4 Flags;        // per effect
    float4 Extra;       // per effect
};

Texture2D<float4> Input : register(t0);

// Where a texture coordinate reads from past the picture: 0 transparent, 1 the edge repeated,
// 2 the picture mirrored.
float4 Edge(float2 uv, uint mode)
{
    if (mode == 2)
    {
        float2 folded = abs(uv);
        uv = 1.0 - abs(1.0 - folded);
    }
    else if (mode == 0 && (any(uv < 0.0) || any(uv > 1.0)))
    {
        return 0.0;
    }

    return Input.SampleLevel(LinearClamp, uv, 0);
}

// Values: xy the offset in texels, z the turn in radians, w the scale. More.xy the centre in
// texels. Flags.x the edge mode.
float4 PsMove(FullScreenVertex input) : SV_TARGET
{
    float2 centre = More.xy;
    float2 p = input.Position.xy - centre - Values.xy;
    float2 from = centre + (Rotate(p, -Values.z) / max(Values.w, 1e-3));
    return Edge(from * TexelSize, Flags.x);
}

// Values.x the strength, 0 to 1. Extra the colour, premultiplied linear. Flags.x 0 over, 1 add.
float4 PsFlash(FullScreenVertex input) : SV_TARGET
{
    float4 picture = Input.Load(int3(input.Position.xy, 0));
    float amount = saturate(Values.x);
    if (Flags.x == 1)
    {
        return float4(picture.rgb + (Extra.rgb * amount * picture.a), picture.a);
    }

    return lerp(picture, Extra, amount * Extra.a);
}

// Values.xy the split in texels (the direction for directional, the strength at the corner for
// radial), More.xy the centre in texels. Flags.x 0 radial, 1 directional.
float4 PsChroma(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    float2 split = Values.xy;
    if (Flags.x == 0)
    {
        float2 fromCentre = p - More.xy;
        float corner = length(Resolution * 0.5);
        split = fromCentre / max(length(fromCentre), 1e-3) * Values.x * (length(fromCentre) / corner);
    }

    float4 red = Input.SampleLevel(LinearClamp, (p + split) * TexelSize, 0);
    float4 green = Input.Load(int3(p, 0));
    float4 blue = Input.SampleLevel(LinearClamp, (p - split) * TexelSize, 0);
    return float4(red.r, green.g, blue.b, max(max(red.a, green.a), blue.a));
}

// Flags.x 0 threshold, 1 inverted threshold, 2 invert, 3 posterize. Values.x the threshold,
// Values.y the posterize levels. Extra the colour of the lights, straight perceptual.
float4 PsImpact(FullScreenVertex input) : SV_TARGET
{
    float4 picture = ToPerceptual(Input.Load(int3(input.Position.xy, 0)));
    float luma = dot(picture.rgb, float3(0.2126, 0.7152, 0.0722));
    float3 result;
    if (Flags.x == 0 || Flags.x == 1)
    {
        // A band a fiftieth wide rather than a step, so the edge is antialiased and the same on every device.
        float light = smoothstep(Values.x - 0.01, Values.x + 0.01, luma);
        light = Flags.x == 1 ? 1.0 - light : light;
        result = light * Extra.rgb;
    }
    else if (Flags.x == 2)
    {
        result = (1.0 - picture.rgb) * Extra.rgb;
    }
    else
    {
        float levels = max(Values.y, 2.0) - 1.0;
        result = (floor((luma * levels) + 0.5) / levels) * Extra.rgb;
    }

    return FromPerceptual(float4(result, picture.a));
}
