// Generators drawn by a pixel shader: gradient, noise and checkerboard. No input; the output is
// frame sized at the working resolution. Colours arrive as straight perceptual (sRGB-encoded) RGBA,
// because a gradient between two colours is expected to look even, which a blend in linear light
// does not.

#include "Effect.hlsli"

cbuffer GeneratorConstants : register(b1)
{
    float4 Values;      // per shader
    float4 More;        // per shader
    uint4 Flags;        // per shader
    float4 Extra;       // per shader
};

// Values colour at the start, More colour at the end, Extra start and end in texels.
// Flags.x 0 linear, 1 radial (start is the centre, the distance to end the radius).
float4 PsGradient(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    float2 from = Extra.xy;
    float2 to = Extra.zw;
    float2 span = to - from;
    float t;

    if (Flags.x == 1)
    {
        t = length(p - from) / max(length(span), 1e-3);
    }
    else
    {
        t = dot(p - from, span) / max(dot(span, span), 1e-6);
    }

    return FromPerceptual(lerp(Values, More, saturate(t)));
}

float ValueNoise(float2 p, float seed)
{
    float2 cell = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = Random(cell, seed);
    float b = Random(cell + float2(1, 0), seed);
    float c = Random(cell + float2(0, 1), seed);
    float d = Random(cell + float2(1, 1), seed);
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float Fractal(float2 p, float seed, int octaves)
{
    float sum = 0.0;
    float amplitude = 0.5;
    float total = 0.0;

    [loop]
    for (int octave = 0; octave < octaves; octave++)
    {
        sum += ValueNoise(p, seed + octave * 13.0) * amplitude;
        total += amplitude;
        p *= 2.0;
        amplitude *= 0.5;
    }

    return sum / total;
}

// Values: x feature size in texels, y contrast, z seed, w octaves. More.xy drift in texels.
// Flags.x 1 for colour noise.
float4 PsNoise(FullScreenVertex input) : SV_TARGET
{
    float2 p = (input.Position.xy + More.xy) / max(Values.x, 1.0);
    int octaves = clamp((int)Values.w, 1, 8);
    float3 n;
    n.r = Fractal(p, Values.z, octaves);
    n.g = Flags.x == 1 ? Fractal(p + 71.3, Values.z + 5.0, octaves) : n.r;
    n.b = Flags.x == 1 ? Fractal(p + 139.7, Values.z + 11.0, octaves) : n.r;
    n = saturate((n - 0.5) * Values.y + 0.5);
    return FromPerceptual(float4(n, 1.0));
}

// Values colour of the squares on the centre, More the other colour. Extra: x square size in
// texels, yz offset in texels.
float4 PsChecker(FullScreenVertex input) : SV_TARGET
{
    float2 p = (input.Position.xy - Resolution * 0.5 - Extra.yz) / max(Extra.x, 1.0);
    float2 cell = floor(p);
    bool first = fmod(abs(cell.x + cell.y), 2.0) < 0.5;
    return FromPerceptual(first ? Values : More);
}
