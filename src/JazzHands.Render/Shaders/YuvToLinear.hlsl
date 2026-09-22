// Samples a decoder's NV12 or P010 texture array slice and writes linear-light RGBA.
//
// This is the first pass of every render graph: everything downstream composites in linear light
// at half float, so the YUV to RGB matrix, the range expansion and the transfer function all
// happen exactly once, here. See the color-science skill.

cbuffer YuvConstants : register(b0)
{
    // Rows of the YUV to RGB matrix for the source colour space.
    float3 MatrixRow0;
    float LumaScale;        // Multiplies the sampled luma before range expansion (10-bit alignment).
    float3 MatrixRow1;
    float LumaOffset;       // Subtracted from luma: 16/255 for limited range, 0 for full.
    float3 MatrixRow2;
    float ChromaOffset;     // Subtracted from chroma: always 0.5.
    float LumaRange;        // 255/219 for limited range, 1 for full.
    float ChromaRange;      // 255/224 for limited range, 1 for full.
    uint TransferFunction;  // 0 = BT.709, 1 = PQ (SMPTE 2084), 2 = HLG, 3 = linear already.
    float PeakLuminance;    // Nits that map to 1.0 for HDR sources.
};

Texture2DArray<float> LumaPlane : register(t0);
Texture2DArray<float2> ChromaPlane : register(t1);
SamplerState LinearSampler : register(s0);

struct VertexOutput
{
    float4 Position : SV_POSITION;
    float2 Uv : TEXCOORD0;
};

// A single oversized triangle covers the target with no vertex or index buffer, and no seam down
// the diagonal the way two triangles have.
VertexOutput VsMain(uint vertexId : SV_VertexID)
{
    VertexOutput output;
    output.Uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(output.Uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return output;
}

// BT.709 inverse transfer. Not sRGB: the knee and the exponent differ, and using one for the
// other is a visible shift in the shadows.
float3 Bt709ToLinear(float3 encoded)
{
    return encoded < 0.081
        ? encoded / 4.5
        : pow(max((encoded + 0.099) / 1.099, 0.0), 1.0 / 0.45);
}

// SMPTE ST 2084 (PQ) inverse EOTF, normalised so PeakLuminance nits becomes 1.0.
float3 PqToLinear(float3 encoded, float peakLuminance)
{
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;

    float3 e = pow(max(encoded, 0.0), 1.0 / m2);
    float3 numerator = max(e - c1, 0.0);
    float3 denominator = c2 - c3 * e;
    float3 linearLight = pow(numerator / max(denominator, 1e-6), 1.0 / m1);

    // PQ encodes absolute luminance up to 10000 nits.
    return linearLight * (10000.0 / max(peakLuminance, 1.0));
}

// ARIB STD-B67 (HLG) inverse OETF.
float3 HlgToLinear(float3 encoded)
{
    const float a = 0.17883277;
    const float b = 0.28466892;
    const float c = 0.55991073;

    return encoded <= 0.5
        ? (encoded * encoded) / 3.0
        : (exp((encoded - c) / a) + b) / 12.0;
}

float4 PsMain(VertexOutput input) : SV_TARGET
{
    float luma = LumaPlane.Sample(LinearSampler, float3(input.Uv, 0.0)).r * LumaScale;
    float2 chroma = ChromaPlane.Sample(LinearSampler, float3(input.Uv, 0.0)).rg * LumaScale;

    // Expand studio swing to full swing before the matrix, or every colour is slightly wrong and
    // black is not black.
    float3 yuv;
    yuv.x = (luma - LumaOffset) * LumaRange;
    yuv.y = (chroma.x - ChromaOffset) * ChromaRange;
    yuv.z = (chroma.y - ChromaOffset) * ChromaRange;

    float3 encoded;
    encoded.r = dot(MatrixRow0, yuv);
    encoded.g = dot(MatrixRow1, yuv);
    encoded.b = dot(MatrixRow2, yuv);

    encoded = saturate(encoded);

    float3 linearLight;
    switch (TransferFunction)
    {
        case 1:
            linearLight = PqToLinear(encoded, PeakLuminance);
            break;
        case 2:
            linearLight = HlgToLinear(encoded);
            break;
        case 3:
            linearLight = encoded;
            break;
        default:
            linearLight = Bt709ToLinear(encoded);
            break;
    }

    // Alpha is premultiplied throughout the graph; decoded video is always opaque.
    return float4(linearLight, 1.0);
}
