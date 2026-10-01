// The colour correction effects: basic correction, wheels, curves, the HSL qualifier, LUTs and
// white balance. One file, one constant buffer shaped for all of them (five float4s and four
// flags), each effect using what it needs. Each adjustment works in its own
// space: light (exposure, white balance, contrast about 18% grey) in linear,
// what a colourist judges by eye (wheels, curves, qualifiers) in perceptual sRGB-encoded values.

#include "Effect.hlsli"

cbuffer GradingConstants : register(b1)
{
    float4 A;
    float4 B;
    float4 C;
    float4 D;
    float4 E;
    uint4 Flags;
};

Texture2D<float4> Input : register(t0);
Texture2D<float4> Curves : register(t1);    // color.curves: row 0 master, R, G, B; row 1 hue-sat, hue-hue, sat-sat
Texture3D<float4> Lut : register(t1);       // color.lut

float4 Straight(float2 position)
{
    return Unpremultiply(Input.Load(int3(position, 0)));
}

float4 Premultiplied(float3 rgb, float alpha)
{
    return Premultiply(float4(rgb, alpha));
}

// A 3x3 matrix in three float4 rows (w unused), as the C# side packs it.
float3 Transform(float3 rgb, float4 row0, float4 row1, float4 row2)
{
    return float3(dot(row0.xyz, rgb), dot(row1.xyz, rgb), dot(row2.xyz, rgb));
}

float3 RgbToHsv(float3 c)
{
    float4 k = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = c.g < c.b ? float4(c.bg, k.wz) : float4(c.gb, k.xy);
    float4 q = c.r < p.x ? float4(p.xyw, c.r) : float4(c.r, p.yzx);
    float d = q.x - min(q.w, q.y);
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + 1e-10)), d / (q.x + 1e-10), q.x);
}

float3 HsvToRgb(float3 c)
{
    float3 p = abs(frac(c.xxx + float3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
    return c.z * lerp(1.0.xxx, saturate(p - 1.0), c.y);
}

// color.basic, in linear light. A..C: the white balance matrix (Bradford, worked out on the CPU
// from temperature and tint). D: x exposure multiplier (2^stops), y contrast (a power about 18%
// grey), z saturation, w vibrance.
float4 PsBasic(FullScreenVertex input) : SV_TARGET
{
    float4 c = Straight(input.Position.xy);
    float3 rgb = max(Transform(c.rgb, A, B, C), 0.0) * D.x;

    rgb = 0.18 * pow(max(rgb, 0.0) / 0.18, D.y);

    float luminance = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    float highest = max(rgb.r, max(rgb.g, rgb.b));
    float saturation = highest > 1e-6 ? (highest - min(rgb.r, min(rgb.g, rgb.b))) / highest : 0.0;

    // Vibrance lifts what is not saturated yet and leaves what is, so skin does not go orange.
    float amount = D.z * (1.0 + D.w * (1.0 - saturation));
    rgb = max(lerp(luminance.xxx, rgb, amount), 0.0);

    return Premultiplied(rgb, c.a);
}

// color.wheels, on perceptual values. A lift, B gamma, C gain, D offset: each r, g, b and a
// master (w) added to all three. E: x saturation, y contrast, z pivot.
float4 PsWheels(FullScreenVertex input) : SV_TARGET
{
    float4 c = ToPerceptual(Input.Load(int3(input.Position.xy, 0)));
    float3 v = c.rgb;

    v += D.rgb + D.w;
    v *= 1.0 + C.rgb + C.w;
    v += (A.rgb + A.w) * (1.0 - v);
    v = pow(max(v, 0.0), 1.0 / max(1.0 + B.rgb + B.w, 0.01));
    v = (v - E.z) * E.y + E.z;

    float luma = dot(v, float3(0.2126, 0.7152, 0.0722));
    v = lerp(luma.xxx, v, E.x);

    return FromPerceptual(float4(v, c.a));
}

// Saturation by a factor towards the colour's own BT.709 luma: 0 is that grey, 1 unchanged.
float3 Saturate709(float3 v, float factor)
{
    float luma = dot(v, float3(0.2126, 0.7152, 0.0722));
    return max(lerp(luma.xxx, v, factor), 0.0);
}

// A baked curve: 1024 texels across 0 to 1, read between texel centres.
float CurveAt(float x, uint row, uint channel)
{
    float u = (saturate(x) * 1023.0 + 0.5) / 1024.0;
    float4 texel = Curves.SampleLevel(LinearClamp, float2(u, (row + 0.5) / 2.0), 0);
    return texel[channel];
}

// color.curves, on perceptual values: the master curve, then each channel's, then the hue and
// saturation curves in HSV. Flags.x 1 when any of the hue or saturation curves is not flat, so
// the common case skips the round trip through HSV.
float4 PsCurves(FullScreenVertex input) : SV_TARGET
{
    float4 c = ToPerceptual(Input.Load(int3(input.Position.xy, 0)));
    float3 v = saturate(c.rgb);

    v = float3(CurveAt(v.r, 0, 0), CurveAt(v.g, 0, 0), CurveAt(v.b, 0, 0));
    v = float3(CurveAt(v.r, 0, 1), CurveAt(v.g, 0, 2), CurveAt(v.b, 0, 3));

    if (Flags.x == 1)
    {
        // Hue curves wrap: 0 and 1 are both red. The hue turns in HSV; saturation moves towards
        // the colour's own luma, because lowering HSV saturation keeps the brightest channel and
        // a bright green would go to white rather than to its grey.
        float3 hsv = RgbToHsv(v);
        float shift = CurveAt(hsv.x, 1, 1) - 0.5;
        if (abs(shift) > 1e-5)
        {
            v = HsvToRgb(float3(frac(hsv.x + shift), hsv.y, hsv.z));
        }

        float factor = 2.0 * CurveAt(hsv.x, 1, 0) * 2.0 * CurveAt(hsv.y, 1, 2);
        v = Saturate709(v, factor);
    }

    return FromPerceptual(float4(v, c.a));
}

// The distance round the hue circle, 0 to 0.5.
float HueDistance(float a, float b)
{
    float d = abs(a - b);
    return min(d, 1.0 - d);
}

// A soft band: 1 between low and high, falling to 0 over softness outside them.
float Band(float value, float low, float high, float softness)
{
    return smoothstep(low - softness, low, value) * (1.0 - smoothstep(high, high + softness, value));
}

// color.hsl, on perceptual values. A: x hue centre (turns), y half width, z softness. B: x
// saturation low, y high, z softness. C: x luma low, y high, z softness. D: x hue shift (turns),
// y saturation multiplier, z lightness offset. Flags: x invert, y view (0 result, 1 matte).
float4 PsHsl(FullScreenVertex input) : SV_TARGET
{
    float4 c = ToPerceptual(Input.Load(int3(input.Position.xy, 0)));
    float3 v = saturate(c.rgb);
    float3 hsv = RgbToHsv(v);
    float luma = dot(v, float3(0.2126, 0.7152, 0.0722));

    float hue = 1.0 - smoothstep(A.y, A.y + max(A.z, 1e-4), HueDistance(hsv.x, A.x));
    float matte = hue * Band(hsv.y, B.x, B.y, max(B.z, 1e-4)) * Band(luma, C.x, C.y, max(C.z, 1e-4));
    matte = Flags.x == 1 ? 1.0 - matte : matte;

    if (Flags.y == 1)
    {
        return float4(SrgbToLinear(matte.xxx), 1.0);
    }

    float3 corrected = Saturate709(HsvToRgb(float3(frac(hsv.x + D.x), hsv.y, hsv.z)), D.y) + D.z;
    return FromPerceptual(float4(lerp(v, corrected, matte), c.a));
}

// ARRI LogC3 at EI 800, the log curve a LUT's input can be in: scene linear to log.
float3 LinearToLogC(float3 x)
{
    const float cut = 0.010591;
    const float a = 5.555556;
    const float b = 0.052272;
    const float c = 0.247190;
    const float d = 0.385537;
    const float e = 5.367655;
    const float f = 0.092809;
    return x > cut ? c * log10(a * x + b) + d : e * x + f;
}

// Tetrahedral interpolation: the cube cell split into six tetrahedra by the order of the
// fractions, four lookups instead of trilinear's eight blended. Smoother on steep LUTs.
float3 Tetrahedral(float3 coordinate, float size)
{
    float3 scaled = saturate(coordinate) * (size - 1.0);
    int3 base = int3(min(floor(scaled), size - 2.0));
    float3 f = scaled - base;

    float3 c000 = Lut.Load(int4(base, 0)).rgb;
    float3 c111 = Lut.Load(int4(base + int3(1, 1, 1), 0)).rgb;

    int3 first;
    int3 second;
    float3 w;
    if (f.r >= f.g && f.g >= f.b)      { first = int3(1, 0, 0); second = int3(1, 1, 0); w = float3(f.r - f.g, f.g - f.b, f.b); }
    else if (f.r >= f.b && f.b >= f.g) { first = int3(1, 0, 0); second = int3(1, 0, 1); w = float3(f.r - f.b, f.b - f.g, f.g); }
    else if (f.b >= f.r && f.r >= f.g) { first = int3(0, 0, 1); second = int3(1, 0, 1); w = float3(f.b - f.r, f.r - f.g, f.g); }
    else if (f.g >= f.r && f.r >= f.b) { first = int3(0, 1, 0); second = int3(1, 1, 0); w = float3(f.g - f.r, f.r - f.b, f.b); }
    else if (f.g >= f.b && f.b >= f.r) { first = int3(0, 1, 0); second = int3(0, 1, 1); w = float3(f.g - f.b, f.b - f.r, f.r); }
    else                               { first = int3(0, 0, 1); second = int3(0, 1, 1); w = float3(f.b - f.g, f.g - f.r, f.r); }

    float3 c1 = Lut.Load(int4(base + first, 0)).rgb;
    float3 c2 = Lut.Load(int4(base + second, 0)).rgb;
    return (1.0 - w.x - w.y - w.z) * c000 + w.x * c1 + w.y * c2 + w.z * c111;
}

// color.lut. A: x intensity, y the cube's size, z domain minimum, w domain maximum. Flags: x
// domain (0 sRGB in and out, 1 linear in and out, 2 LogC in and BT.1886 Rec.709 out), y 1 for
// tetrahedral.
float4 PsLut(FullScreenVertex input) : SV_TARGET
{
    float4 c = Straight(input.Position.xy);
    float3 linearLight = max(c.rgb, 0.0);

    float3 encoded = Flags.x == 1 ? linearLight
        : Flags.x == 2 ? LinearToLogC(linearLight)
        : LinearToSrgb(linearLight);

    float3 coordinate = saturate((encoded - A.z) / max(A.w - A.z, 1e-6));
    float size = A.y;
    float3 looked = Flags.y == 1
        ? Tetrahedral(coordinate, size)
        : Lut.SampleLevel(LinearClamp, (coordinate * (size - 1.0) + 0.5) / size, 0).rgb;

    float3 decoded = Flags.x == 1 ? looked
        : Flags.x == 2 ? Bt1886ToLinear(saturate(looked))
        : SrgbToLinear(saturate(looked));

    return Premultiplied(lerp(linearLight, decoded, A.x), c.a);
}

// color.white-balance, in linear light: A..C a Bradford matrix that takes the picked neutral to
// D65 white, mixed by D.x.
float4 PsWhiteBalance(FullScreenVertex input) : SV_TARGET
{
    float4 c = Straight(input.Position.xy);
    float3 balanced = max(Transform(c.rgb, A, B, C), 0.0);
    return Premultiplied(lerp(c.rgb, balanced, D.x), c.a);
}
