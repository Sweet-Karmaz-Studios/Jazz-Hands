// The single layer preview path Phase 09 plays through until the compositor arrives in Phase 10.
//
// PsFrame draws one decoded frame, in whichever of the texture layouts a FrameTexture holds, as
// display-referred BT.709 into an eight bit target. PsBlit copies such a target into a preview
// surface at a size and position. Both share the quad vertex shader, which places a rectangle of
// the source onto a rectangle of the target, so fitting, letterboxing and zoom are all a matter
// of two rectangles.

cbuffer QuadConstants : register(b0)
{
    float4 DestRect;        // left, top, right, bottom as fractions of the target
    float4 SourceRect;      // left, top, right, bottom as texture coordinates
    float3 MatrixRow0;
    float SampleScale;      // turns a UNORM sample into the code value range the matrix expects
    float3 MatrixRow1;
    float LumaOffset;       // 16/255 for limited range, 0 for full
    float3 MatrixRow2;
    float ChromaOffset;     // always 0.5
    float LumaRange;        // 255/219 for limited range, 1 for full
    float ChromaRange;      // 255/224 for limited range, 1 for full
    uint TransferFunction;  // 0 = BT.709, 1 = PQ, 2 = HLG, 3 = linear
    uint IsPlanar;          // 1 when U and V are separate planes, 0 when they are interleaved
};

Texture2D<float4> PlaneY : register(t0);
Texture2D<float4> PlaneA : register(t1);
Texture2D<float4> PlaneB : register(t2);
SamplerState LinearSampler : register(s0);

struct VertexOutput
{
    float4 Position : SV_POSITION;
    float2 Uv : TEXCOORD0;
};

// Four vertices as a strip, no buffers: the corner comes from the vertex id.
VertexOutput VsQuad(uint vertexId : SV_VertexID)
{
    float2 corner = float2(vertexId & 1, vertexId >> 1);
    float2 target = lerp(DestRect.xy, DestRect.zw, corner);

    VertexOutput output;
    output.Position = float4(target.x * 2.0 - 1.0, 1.0 - target.y * 2.0, 0.0, 1.0);
    output.Uv = lerp(SourceRect.xy, SourceRect.zw, corner);
    return output;
}

// SMPTE ST 2084 to absolute luminance in nits.
float3 PqToNits(float3 encoded)
{
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;

    float3 e = pow(max(encoded, 0.0), 1.0 / m2);
    return 10000.0 * pow(max(e - c1, 0.0) / max(c2 - c3 * e, 1e-6), 1.0 / m1);
}

// ARIB STD-B67 to display luminance in nits, for a 1000 nit display with the system gamma of 1.2.
float3 HlgToNits(float3 encoded)
{
    const float a = 0.17883277;
    const float b = 0.28466892;
    const float c = 0.55991073;

    float3 scene = encoded <= 0.5 ? (encoded * encoded) / 3.0 : (exp((encoded - c) / a) + b) / 12.0;
    float luminance = dot(scene, float3(0.2627, 0.6780, 0.0593));
    return 1000.0 * scene * pow(max(luminance, 1e-6), 0.2);
}

// A stand-in for the tone mapping Phase 17 brings: reference white at 203 nits becomes 1, an
// extended Reinhard curve on luminance rolls a 1000 nit highlight into the top of the range, and
// the BT.2020 primaries are brought into BT.709. Enough that HDR footage reads correctly in the
// preview rather than as a washed out grey.
float3 HdrToDisplay(float3 nits)
{
    const float white = 1000.0 / 203.0;

    float3 relative = nits / 203.0;
    float luminance = dot(relative, float3(0.2627, 0.6780, 0.0593));
    float mapped = luminance * (1.0 + luminance / (white * white)) / (1.0 + luminance);
    float3 scaled = relative * (mapped / max(luminance, 1e-6));

    float3 bt709;
    bt709.r = dot(scaled, float3(1.6605, -0.5876, -0.0728));
    bt709.g = dot(scaled, float3(-0.1246, 1.1329, -0.0083));
    bt709.b = dot(scaled, float3(-0.0182, -0.1006, 1.1187));

    return pow(saturate(bt709), 1.0 / 2.4);
}

float4 PsFrame(VertexOutput input) : SV_TARGET
{
    float luma = PlaneY.Sample(LinearSampler, input.Uv).r * SampleScale;

    float4 first = PlaneA.Sample(LinearSampler, input.Uv);
    float2 chroma = IsPlanar != 0
        ? float2(first.r, PlaneB.Sample(LinearSampler, input.Uv).r)
        : first.rg;
    chroma *= SampleScale;

    float3 yuv;
    yuv.x = (luma - LumaOffset) * LumaRange;
    yuv.y = (chroma.x - ChromaOffset) * ChromaRange;
    yuv.z = (chroma.y - ChromaOffset) * ChromaRange;

    float3 encoded = saturate(float3(dot(MatrixRow0, yuv), dot(MatrixRow1, yuv), dot(MatrixRow2, yuv)));

    float3 display;
    switch (TransferFunction)
    {
        case 1:
            display = HdrToDisplay(PqToNits(encoded));
            break;
        case 2:
            display = HdrToDisplay(HlgToNits(encoded));
            break;
        case 3:
            display = pow(encoded, 1.0 / 2.4);
            break;
        default:
            // SDR video is already display referred, and so is the preview it is going to.
            display = encoded;
            break;
    }

    return float4(display, 1.0);
}

float4 PsBlit(VertexOutput input) : SV_TARGET
{
    return float4(PlaneY.Sample(LinearSampler, input.Uv).rgb, 1.0);
}
