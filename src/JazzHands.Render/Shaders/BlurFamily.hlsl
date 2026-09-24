// Directional and radial blur, and the passes glow, sharpen and drop shadow add around a Gaussian
// (GaussianBlur.hlsl). The constants are EffectValues' layout: two float4s and four flags.

#include "Effect.hlsli"

cbuffer BlurFamilyConstants : register(b1)
{
    float4 Values;      // per shader
    float4 More;        // per shader
    uint4 Flags;        // per shader
};

Texture2D<float4> Input : register(t0);
Texture2D<float4> Second : register(t1);

// Values.xy one step along the blur as a texture coordinate; Flags.x the steps each side.
// A box of taps along a line: a motion smear.
float4 PsDirectional(FullScreenVertex input) : SV_TARGET
{
    int taps = (int)Flags.x;
    float4 sum = 0.0;

    [loop]
    for (int tap = -taps; tap <= taps; tap++)
    {
        sum += Input.SampleLevel(LinearClamp, input.Uv + Values.xy * tap, 0);
    }

    return sum / (2.0 * taps + 1.0);
}

// Values: xy centre as a texture coordinate, z amount (zoom: fraction scaled towards the centre;
// spin: radians turned either way). Flags.x 0 zoom, 1 spin; Flags.y the taps.
float4 PsRadial(FullScreenVertex input) : SV_TARGET
{
    int taps = max((int)Flags.y, 2);
    float aspect = Resolution.x / Resolution.y;
    float2 offset = (input.Uv - Values.xy) * float2(aspect, 1.0);
    float4 sum = 0.0;

    [loop]
    for (int tap = 0; tap < taps; tap++)
    {
        float t = (float)tap / (taps - 1);
        float2 moved;
        if (Flags.x == 0)
        {
            moved = offset * (1.0 - Values.z * t);
        }
        else
        {
            moved = Rotate(offset, Values.z * (t * 2.0 - 1.0));
        }

        sum += Input.SampleLevel(LinearClamp, Values.xy + moved / float2(aspect, 1.0), 0);
    }

    return sum / taps;
}

// t0 the picture, t1 it blurred. Values: x amount, y threshold (brightness difference below which
// nothing is sharpened, so flat areas and noise are left alone).
float4 PsSharpen(FullScreenVertex input) : SV_TARGET
{
    int3 texel = int3(input.Position.xy, 0);
    float4 original = ToPerceptual(Input.Load(texel));
    float4 soft = ToPerceptual(Second.Load(texel));
    float3 detail = original.rgb - soft.rgb;
    float weight = smoothstep(Values.y, Values.y + 0.02, abs(Luma709(detail)));
    original.rgb += detail * Values.x * (Values.y > 0.0 ? weight : 1.0);
    return FromPerceptual(original);
}

// What glows: light above Values.x (threshold), faded in over Values.y (knee), in linear light.
float4 PsBright(FullScreenVertex input) : SV_TARGET
{
    float4 c = Input.Load(int3(input.Position.xy, 0));
    float luma = Luma709(Unpremultiply(c).rgb);
    return c * smoothstep(Values.x, Values.x + max(Values.y, 1e-3), luma);
}

// t0 the picture, t1 its bright parts blurred. Values.x intensity, More.rgb the tint. The glow is
// added light: it brightens what is under a transparent part of the picture too.
float4 PsAddGlow(FullScreenVertex input) : SV_TARGET
{
    int3 texel = int3(input.Position.xy, 0);
    float4 c = Input.Load(texel);
    float4 glow = Second.Load(texel);
    return float4(c.rgb + glow.rgb * More.rgb * Values.x, c.a);
}

// The shadow's shape: the picture's alpha, moved by Values.xy (texture coordinates), in the
// colour More (premultiplied linear, opacity included).
float4 PsShadow(FullScreenVertex input) : SV_TARGET
{
    float2 uv = input.Uv - Values.xy;
    float alpha = any(uv < 0.0) || any(uv > 1.0) ? 0.0 : Input.SampleLevel(LinearClamp, uv, 0).a;
    return More * alpha;
}

// t0 over t1, premultiplied.
float4 PsOver(FullScreenVertex input) : SV_TARGET
{
    int3 texel = int3(input.Position.xy, 0);
    float4 top = Input.Load(texel);
    return top + Second.Load(texel) * (1.0 - top.a);
}
