// Masks. Each mask shape is rasterized by Direct2D into the alpha of its own texture, feathered
// here with a separable Gaussian, and combined into the layer's matte one mask at a time. The
// matte's alpha is what Composite multiplies the layer's coverage by.

#include "Common.hlsli"

#define MASK_ADD 0
#define MASK_SUBTRACT 1
#define MASK_INTERSECT 2

cbuffer MatteConstants : register(b0)
{
    float2 Direction;       // one texel along the blur axis
    float Sigma;            // Gaussian width in texels; 0 copies
    int Radius;             // taps each side
    uint MaskMode;
    uint Invert;
    float MaskOpacity;
    uint First;             // 1 for the first mask, which starts from an empty matte
};

Texture2D<float4> Input : register(t0);
Texture2D<float4> Current : register(t1);

FullScreenVertex VsMain(uint vertexId : SV_VertexID)
{
    return FullScreenTriangle(vertexId);
}

// One direction of the blur, on alpha only.
float4 PsBlur(FullScreenVertex input) : SV_TARGET
{
    int3 texel = int3(input.Position.xy, 0);

    if (Sigma <= 0.0)
    {
        return Input.Load(texel);
    }

    float2 uv = input.Uv;
    float weight = 1.0;
    float sum = Input.SampleLevel(LinearClamp, uv, 0).a;

    [loop]
    for (int tap = 1; tap <= Radius; tap++)
    {
        float w = exp(-0.5 * tap * tap / (Sigma * Sigma));
        sum += (Input.SampleLevel(LinearClamp, uv + Direction * tap, 0).a
            + Input.SampleLevel(LinearClamp, uv - Direction * tap, 0).a) * w;
        weight += 2.0 * w;
    }

    return float4(1.0, 1.0, 1.0, sum / weight);
}

// Folds one feathered mask (t0) into the matte so far (t1).
float4 PsCombine(FullScreenVertex input) : SV_TARGET
{
    int3 texel = int3(input.Position.xy, 0);
    float mask = Input.Load(texel).a;

    if (Invert != 0)
    {
        mask = 1.0 - mask;
    }

    mask *= MaskOpacity;

    // The first mask sets the shape: added to nothing it is itself, and subtracted from or
    // intersected with nothing it leaves everything, as editors do when a lone mask is set to
    // subtract.
    float current = First != 0 ? (MaskMode == MASK_ADD ? 0.0 : 1.0) : Current.Load(texel).a;

    float result;
    switch (MaskMode)
    {
        case MASK_SUBTRACT:
            result = current * (1.0 - mask);
            break;
        case MASK_INTERSECT:
            result = current * mask;
            break;
        default:
            result = max(current, mask);
            break;
    }

    return float4(1.0, 1.0, 1.0, result);
}
