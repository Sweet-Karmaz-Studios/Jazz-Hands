// The looks: glitch, CRT, VHS, a light sweep, letterbox bars, bloom's combine and deband. What moves
// over time is worked out on the CPU (a step number, a band's position), and randomness is the
// integer hash, so a frame is the same on every device and whether it is played to or rendered cold.

#include "Effect.hlsli"

cbuffer LooksConstants : register(b1)
{
    float4 Values;      // per effect, see each shader
    float4 More;        // per effect
    uint4 Flags;        // per effect
    float4 Extra;       // per effect
};

static const float Pi = 3.14159265;

Texture2D<float4> Input : register(t0);
Texture2D<float4> Second : register(t1);
Texture2D<float4> Third : register(t2);
Texture2D<float4> Fourth : register(t3);

// A repeatable random number from 0 to 1 for a whole-number cell and a seed.
float Chance(float2 p, uint seed)
{
    int2 cell = int2(floor(p));
    uint h = PcgHash(asuint(cell.x) ^ PcgHash(asuint(cell.y) ^ PcgHash(seed)));
    return (h & 0xFFFFFF) / 16777215.0;
}

float4 At(float2 p)
{
    return Input.SampleLevel(LinearClamp, p * TexelSize, 0);
}

// Values: x amount 0 to 1, y block size, z the RGB split, w the scanline jitter, all in texels.
// Flags.x the seed for this step of the glitch (it changes Speed times a second).
float4 PsGlitch(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    uint seed = Flags.x;
    float amount = saturate(Values.x);
    float block = max(Values.y, 2.0);

    // Rows torn sideways.
    float band = floor(p.y / (block * 0.5));
    float2 from = p;
    if (Chance(float2(0.0, band), seed) > 1.0 - (amount * 0.6))
    {
        from.x += (Chance(float2(1.0, band), seed) - 0.5) * block * 8.0 * amount;
    }

    // Blocks that show a piece of the picture from somewhere near.
    float2 cell = floor(p / block);
    if (Chance(cell, seed ^ 0x9E3779B9u) < amount * 0.2)
    {
        from += (float2(Chance(cell + 17.0, seed), Chance(cell + 31.0, seed)) - 0.5) * block * 6.0;
    }

    // Every line nudged a little.
    from.x += (Chance(float2(2.0, p.y), seed) - 0.5) * 2.0 * Values.w * amount;

    float2 split = float2(Values.z * amount, 0.0);
    float4 left = At(from + split);
    float4 middle = At(from);
    float4 right = At(from - split);
    float alpha = max(middle.a, max(left.a, right.a));
    return float4(min(float3(left.r, middle.g, right.b), alpha), alpha);
}

// Values: x curvature, y scanline strength, z scanlines down the frame, w the mask's strength.
// More: x bloom, y vignette, z the mask's column width in texels.
float4 PsCrt(FullScreenVertex input) : SV_TARGET
{
    float2 centred = (input.Uv * 2.0) - 1.0;
    centred *= 1.0 + (Values.x * 0.25 * dot(centred.yx, centred.yx));
    float2 uv = (centred * 0.5) + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0))
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    float4 c = Input.SampleLevel(LinearClamp, uv, 0);

    // A little light bleeding into its neighbours, as a tube's phosphor glows.
    float2 reach = TexelSize * 2.0 * QualityScale;
    float4 glow = 0.0;
    glow += Input.SampleLevel(LinearClamp, uv + float2(reach.x, 0.0), 0);
    glow += Input.SampleLevel(LinearClamp, uv - float2(reach.x, 0.0), 0);
    glow += Input.SampleLevel(LinearClamp, uv + float2(0.0, reach.y), 0);
    glow += Input.SampleLevel(LinearClamp, uv - float2(0.0, reach.y), 0);
    c.rgb += glow.rgb * 0.25 * More.x;

    // Scanlines, keeping the average brightness.
    float scan = 0.5 - (0.5 * cos(uv.y * Values.z * 2.0 * Pi));
    c.rgb *= (1.0 - (Values.y * scan)) / max(1.0 - (Values.y * 0.5), 1e-3);

    // An aperture grille: columns of red, green and blue.
    uint column = (uint)floor(input.Position.x / max(More.z, 1.0)) % 3u;
    float dim = 1.0 - Values.w;
    float3 mask = column == 0u ? float3(1.0, dim, dim) : column == 1u ? float3(dim, 1.0, dim) : float3(dim, dim, 1.0);
    c.rgb *= mask * (3.0 / (1.0 + (2.0 * dim)));

    c.rgb *= saturate(1.0 - (More.y * 0.5 * dot(centred, centred)));
    return c;
}

// Values: x wobble, y chroma bleed, both in texels; z noise 0 to 1; w tracking noise 0 to 1.
// More: x seconds, y where the tracking band is down the frame (0 to 1), z saturation.
// Flags.x the seed for this frame.
float4 PsVhs(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    uint seed = Flags.x;
    float height = Resolution.y;

    // The picture wobbles side to side, slowly and line by line.
    float2 from = p;
    from.x += sin((p.y * 0.021 / QualityScale) + (More.x * 3.1)) * Values.x * 0.5;
    from.x += (Chance(float2(3.0, floor(p.y / (2.0 * QualityScale))), seed) - 0.5) * Values.x * 0.4;

    // The tracking band: a stripe near where the tape is misread, torn and full of noise.
    float band = abs((p.y / height) - More.y);
    float torn = saturate(1.0 - (band / 0.04)) * Values.w;
    from.x += torn * (Chance(float2(4.0, floor(p.y / QualityScale)), seed) - 0.3) * 40.0 * QualityScale;

    float4 centre = ToPerceptual(At(from));
    float luma = dot(centre.rgb, float3(0.2126, 0.7152, 0.0722));

    // Colour smears to the right: the chroma is the average of a run of pixels to the left.
    float3 chroma = 0.0;
    [unroll]
    for (int tap = 0; tap < 8; tap++)
    {
        float3 c = ToPerceptual(At(from - float2(Values.y * tap / 7.0, 0.0))).rgb;
        chroma += c - dot(c, float3(0.2126, 0.7152, 0.0722));
    }

    float3 rgb = luma + (chroma / 8.0 * More.z);

    // Grain over everything, and white streaks in the band.
    float grain = (Chance(p, seed ^ 0x51ED27u) - 0.5) * Values.z * 0.25;
    float streak = Chance(float2(floor(p.x / (6.0 * QualityScale)), floor(p.y / QualityScale)), seed ^ 0x1B873593u) > 0.93 ? torn : 0.0;
    rgb = saturate(rgb + grain + streak);
    return FromPerceptual(float4(rgb, centre.a));
}

// Values: x the band's middle along the direction, y its width, both in texels; z intensity;
// w softness 0 to 1. More.xy the direction. Extra the colour, straight linear. The shine is added
// light, only where the picture is.
float4 PsLightSweep(FullScreenVertex input) : SV_TARGET
{
    float4 c = Input.Load(int3(input.Position.xy, 0));
    float along = dot(input.Position.xy, More.xy);
    float t = abs(along - Values.x) / max(Values.y * 0.5, 1e-3);
    float shine = 1.0 - smoothstep(1.0 - saturate(Values.w), 1.0, t);
    return float4(c.rgb + (Extra.rgb * shine * Values.z * c.a), c.a);
}

// Values: x the bars' height top and bottom, y their width left and right, in texels. Extra the
// colour, premultiplied linear.
float4 PsLetterbox(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    float4 c = Input.Load(int3(p, 0));
    float top = saturate(Values.x - p.y + 0.5) + saturate(p.y - (Resolution.y - Values.x) + 0.5);
    float side = saturate(Values.y - p.x + 0.5) + saturate(p.x - (Resolution.x - Values.y) + 0.5);
    float bar = saturate(top + side);
    return lerp(c, Extra, bar);
}

// t0 the picture, t1 to t3 its bright parts blurred small, middling and wide. Values.x intensity,
// More.rgb the tint. Added light, like the glow.
float4 PsBloom(FullScreenVertex input) : SV_TARGET
{
    int3 texel = int3(input.Position.xy, 0);
    float4 c = Input.Load(texel);
    float3 bloom = (Second.Load(texel).rgb * 0.45) + (Third.Load(texel).rgb * 0.35) + (Fourth.Load(texel).rgb * 0.2);
    return float4(c.rgb + (bloom * More.rgb * Values.x), c.a);
}

// Values: x how close (perceptual) a neighbour has to be to count as the same flat area, y the
// radius in texels, z the dither in 8-bit steps. Flags.x the seed for this frame.
float4 PsDeband(FullScreenVertex input) : SV_TARGET
{
    float2 p = input.Position.xy;
    uint seed = Flags.x;
    float4 centre = ToPerceptual(Input.Load(int3(p, 0)));

    // Four neighbours at a random angle and distance: if all are close, this is a smooth area
    // cut into bands, and their average is what it should have been.
    float angle = Chance(p, seed) * 2.0 * Pi;
    float distance = max(Values.y * (0.5 + (0.5 * Chance(p + 0.5, seed ^ 0x2545F491u))), 1.0);
    float2 a = float2(cos(angle), sin(angle)) * distance;
    float2 b = float2(-a.y, a.x);
    float4 n0 = ToPerceptual(At(p + a));
    float4 n1 = ToPerceptual(At(p - a));
    float4 n2 = ToPerceptual(At(p + b));
    float4 n3 = ToPerceptual(At(p - b));
    float3 reach = max(max(abs(n0.rgb - centre.rgb), abs(n1.rgb - centre.rgb)), max(abs(n2.rgb - centre.rgb), abs(n3.rgb - centre.rgb)));
    float even = step(max(reach.r, max(reach.g, reach.b)), Values.x);
    float3 rgb = lerp(centre.rgb, (centre.rgb + n0.rgb + n1.rgb + n2.rgb + n3.rgb) / 5.0, even);

    // Triangular dither, so the smooth result is not banded again by 8-bit output.
    float dither = (Chance(p, seed ^ 0x68E31DA4u) + Chance(p, seed ^ 0xB5297A4Du) - 1.0) * Values.z / 255.0;
    return FromPerceptual(float4(rgb + dither, centre.a));
}
