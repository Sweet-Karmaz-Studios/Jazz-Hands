// The scopes: histogram, luma waveform, RGB parade and vectorscope, measured on the output frame
// (BT.1886 encoded, as delivered), so they show the signal an export will carry. One pass counts
// every pixel into a raw buffer with atomic adds; one pass per picture turns the counts into an
// image. See the color-science skill. A frame over a megapixel is sampled every other pixel each
// way: the pictures are 512 and 256 across, so a quarter of a 1080p frame still puts several
// samples in every cell, and the histograms keep their shape.
//
// The counts buffer, in uints:
//   [0, 1024)                              histogram: red, green, blue, luma, 256 bins each
//   [WaveBase, + 256 * WaveWidth)          luma waveform, row 0 the top (white)
//   [ParadeBase, + 256 * 3 * ParadeWidth)  parade, red then green then blue side by side
//   [VectorBase, + 256 * 256)              vectorscope, Cb across and Cr up

#include "Common.hlsli"

cbuffer ScopeConstants : register(b0)
{
    uint Width;          // the frame
    uint Height;
    uint WaveWidth;      // columns of the waveform
    uint ParadeWidth;    // columns of each channel of the parade
    uint WaveBase;       // where each part of the counts starts, in uints
    uint ParadeBase;
    uint VectorBase;
    uint Step;           // 1 for every pixel, 2 for every other pixel each way on large frames
    float WaveGain;      // how quickly a bin brightens, from the samples a column gets
    float VectorGain;
    float ParadeGain;    // the parade's own: its columns are fewer, so each gets more samples
    float Padding;
};

Texture2D<float4> Frame : register(t0);
RWByteAddressBuffer Counts : register(u0);
RWTexture2D<unorm float4> Picture : register(u1);

void Count(uint index)
{
    uint ignored;
    Counts.InterlockedAdd(index * 4, 1, ignored);
}

// Waveform and parade columns are fewer than the frame's, and not a whole number of pixels
// each, so some columns are fed by one more frame column than their neighbours. Counted as they
// are, that would draw stripes; the draw passes scale each column by how many it was fed.
uint Fed(uint column, uint columns)
{
    // Frame columns x with floor(x * columns / Width) == column, of those sampled every Step.
    uint first = ((column * Width) + columns - 1) / columns;
    uint next = (((column + 1) * Width) + columns - 1) / columns;
    return ((next + Step - 1) / Step) - ((first + Step - 1) / Step);
}

// How much brighter a column's counts are drawn for being fed less than the average.
float Evened(uint column, uint columns)
{
    float average = ((Width + Step - 1) / Step) / (float)columns;
    return average / max(Fed(column, columns), 1u);
}

uint Bin(float value)
{
    return (uint)round(saturate(value) * 255.0);
}

[numthreads(16, 16, 1)]
void CsAccumulate(uint3 id : SV_DispatchThreadID)
{
    uint2 pixel = id.xy * Step;
    if (pixel.x >= Width || pixel.y >= Height)
    {
        return;
    }

    float3 c = saturate(Frame.Load(int3(pixel, 0)).rgb);
    float luma = dot(c, float3(0.2126, 0.7152, 0.0722));
    uint3 bins = uint3(Bin(c.r), Bin(c.g), Bin(c.b));
    uint lumaBin = Bin(luma);

    Count(bins.r);
    Count(256 + bins.g);
    Count(512 + bins.b);
    Count(768 + lumaBin);

    uint column = pixel.x * WaveWidth / Width;
    Count(WaveBase + ((255 - lumaBin) * WaveWidth) + column);

    uint paradeColumn = pixel.x * ParadeWidth / Width;
    uint paradeRow = 3 * ParadeWidth;
    Count(ParadeBase + ((255 - bins.r) * paradeRow) + paradeColumn);
    Count(ParadeBase + ((255 - bins.g) * paradeRow) + ParadeWidth + paradeColumn);
    Count(ParadeBase + ((255 - bins.b) * paradeRow) + (2 * ParadeWidth) + paradeColumn);

    // BT.709 Cb and Cr, -0.5 to 0.5; red sits up and to the left, as on every vectorscope.
    float cb = (c.b - luma) / 1.8556;
    float cr = (c.r - luma) / 1.5748;
    uint u = Bin(cb + 0.5);
    uint v = Bin(0.5 - cr);
    Count(VectorBase + (v * 256) + u);
}

uint Read(uint index)
{
    return Counts.Load(index * 4);
}

// A bin's brightness: quick to show a trace, slow to saturate, as a phosphor would.
float Glow(uint count, float gain)
{
    return 1.0 - exp(-(float)count * gain);
}

// The pictures are written into RGBA8 textures with blue and red swapped, so the bytes read back
// are BGRA, which is what WPF draws.
float4 Bgra(float3 rgb, float alpha)
{
    return float4(rgb.b, rgb.g, rgb.r, alpha);
}

[numthreads(16, 16, 1)]
void CsWaveform(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= WaveWidth || id.y >= 256)
    {
        return;
    }

    float glow = Glow(Read(WaveBase + (id.y * WaveWidth) + id.x), WaveGain * Evened(id.x, WaveWidth));
    Picture[id.xy] = Bgra(float3(0.55, 1.0, 0.65) * glow, glow);
}

[numthreads(16, 16, 1)]
void CsParade(uint3 id : SV_DispatchThreadID)
{
    uint row = 3 * ParadeWidth;
    if (id.x >= row || id.y >= 256)
    {
        return;
    }

    uint channel = id.x / ParadeWidth;
    float3 tint = channel == 0 ? float3(1.0, 0.3, 0.3) : channel == 1 ? float3(0.35, 1.0, 0.4) : float3(0.4, 0.55, 1.0);
    float glow = Glow(Read(ParadeBase + (id.y * row) + id.x), ParadeGain * Evened(id.x % ParadeWidth, ParadeWidth));
    Picture[id.xy] = Bgra(tint * glow, glow);
}

[numthreads(16, 16, 1)]
void CsVector(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= 256 || id.y >= 256)
    {
        return;
    }

    float glow = Glow(Read(VectorBase + (id.y * 256) + id.x), VectorGain);

    // Each point in the colour it stands for, at full brightness, so the trace reads as hues.
    float cb = (id.x / 255.0) - 0.5;
    float cr = 0.5 - (id.y / 255.0);
    float3 hue = saturate(float3(0.5 + 1.5748 * cr, 0.5 - 0.1873 * cb - 0.4681 * cr, 0.5 + 1.8556 * cb));
    Picture[id.xy] = Bgra(lerp(1.0.xxx, hue, 0.6) * glow, glow);
}
