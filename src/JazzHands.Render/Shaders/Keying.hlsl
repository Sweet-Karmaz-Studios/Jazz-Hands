// Chroma and luma keys. The key works on perceptual (sRGB-encoded) colour turned into BT.709
// Y'CbCr: a colour's distance from the key colour in the CbCr plane is how different its hue and
// saturation are, whatever its brightness, which is what makes a lit and a shadowed part of a green
// screen key alike. The chroma key runs as passes: PsKey makes the matte and removes spill, PsMorph
// erodes or grows the matte, PsFeather softens it, PsFinish puts the picture back together.

#include "Effect.hlsli"

cbuffer KeyingConstants : register(b1)
{
    float4 Values;      // per shader
    float4 More;        // per shader
    uint4 Flags;        // per shader
};

Texture2D<float4> Input : register(t0);
Texture2D<float4> Original : register(t1);

float3 RgbToYcc(float3 rgb)
{
    float y = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    return float3(y, (rgb.b - y) / 1.8556, (rgb.r - y) / 1.5748);
}

float3 YccToRgb(float3 ycc)
{
    float r = ycc.x + 1.5748 * ycc.z;
    float b = ycc.x + 1.8556 * ycc.y;
    float g = (ycc.x - 0.2126 * r - 0.0722 * b) / 0.7152;
    return float3(r, g, b);
}

// Values: xy the key colour's CbCr, z tolerance, w softness. More.x spill suppression 0..1.
// Out: straight perceptual colour with the spill taken out, and alpha the matte times the input's.
float4 PsKey(FullScreenVertex input) : SV_TARGET
{
    float4 c = ToPerceptual(Input.Load(int3(input.Position.xy, 0)));
    float3 ycc = RgbToYcc(c.rgb);

    float distance = length(ycc.yz - Values.xy);
    float matte = smoothstep(Values.z, Values.z + max(Values.w, 1e-4), distance);

    // Spill: whatever of the key's hue is left in the colour is taken out towards grey, so green
    // light bounced onto an edge or a face does not survive the key.
    float2 direction = normalize(Values.xy + 1e-6);
    float along = dot(ycc.yz, direction);
    if (along > 0.0)
    {
        ycc.yz -= direction * along * More.x;
    }

    return float4(saturate(YccToRgb(ycc)), matte * c.a);
}

// Erode or grow the matte by the minimum or maximum over a line of taps. Values.xy one texel along
// the line; Flags.x the taps each side; Flags.y 0 to erode (shrink), 1 to grow.
float4 PsMorph(FullScreenVertex input) : SV_TARGET
{
    float4 centre = Input.Load(int3(input.Position.xy, 0));
    float alpha = centre.a;
    int taps = (int)Flags.x;

    [loop]
    for (int tap = -taps; tap <= taps; tap++)
    {
        float a = Input.SampleLevel(PointClamp, input.Uv + Values.xy * tap, 0).a;
        alpha = Flags.y == 0 ? min(alpha, a) : max(alpha, a);
    }

    return float4(centre.rgb, alpha);
}

// A Gaussian on the matte alone. Values.xy one texel along the line, z sigma; Flags.x taps.
float4 PsFeather(FullScreenVertex input) : SV_TARGET
{
    float4 centre = Input.Load(int3(input.Position.xy, 0));
    float sum = centre.a;
    float weight = 1.0;
    int taps = (int)Flags.x;

    [loop]
    for (int tap = 1; tap <= taps; tap++)
    {
        float w = exp(-0.5 * tap * tap / (Values.z * Values.z));
        sum += (Input.SampleLevel(PointClamp, input.Uv + Values.xy * tap, 0).a + Input.SampleLevel(PointClamp, input.Uv - Values.xy * tap, 0).a) * w;
        weight += 2.0 * w;
    }

    return float4(centre.rgb, sum / weight);
}

// The keyed picture (t0: straight perceptual colour and the final matte) as premultiplied linear
// light, or with Flags.x 1 the matte itself, white kept and black keyed out, for checking a key.
float4 PsFinish(FullScreenVertex input) : SV_TARGET
{
    float4 keyed = Input.Load(int3(input.Position.xy, 0));
    if (Flags.x == 1)
    {
        return float4(SrgbToLinear(keyed.aaa), 1.0);
    }

    return FromPerceptual(keyed);
}

// Values: x threshold, y softness. Flags.x 1 to key out the bright rather than the dark;
// Flags.y 1 to show the matte.
float4 PsLuma(FullScreenVertex input) : SV_TARGET
{
    float4 c = ToPerceptual(Input.Load(int3(input.Position.xy, 0)));
    float luma = Luma709(c.rgb);
    float matte = smoothstep(Values.x, Values.x + max(Values.y, 1e-4), luma);
    if (Flags.x == 1)
    {
        matte = 1.0 - matte;
    }

    if (Flags.y == 1)
    {
        float shown = matte * c.a;
        return float4(SrgbToLinear(shown.xxx), 1.0);
    }

    c.a *= matte;
    return FromPerceptual(c);
}
