// ACES 2.0 (Phase 44): the output transform, its inverse, ACEScct and the input transforms, per
// pixel. A port of AcesOutputTransform.cs and AcesInput.cs, which are ports of the Academy's
// reference (aces-core, Apache 2.0) and checked against OpenColorIO; this file is checked against
// them in AcesShaderTests. The hue tables and every constant are worked out once on the CPU
// (AcesGpu) and arrive here as AcesConstants and a 362 by 2 table: row 0 holds each hue's gamut
// cusp J and M, the hue itself and the upper hull's 1/gamma, row 1 the reach gamut's M.
//
// A file that includes this defines ACES_CONSTANTS_REGISTER and ACES_TABLE_REGISTER first.

#ifndef ACES_HLSLI
#define ACES_HLSLI

cbuffer AcesConstants : register(ACES_CONSTANTS_REGISTER)
{
    float4 InRgbToCam[3];      // AP0 to CAM16, per the CTL's row-vector convention (rows)
    float4 InCamToRgb[3];
    float4 LimRgbToCam[3];     // the limiting primaries to CAM16
    float4 LimCamToRgb[3];
    float4 ConeToAab[3];
    float4 AabToCone[3];
    float4 LimitToDisplay[3];
    float4 DisplayToLimit[3];
    float4 Ap0ToAp1[3];
    float4 Ap1ToAp0[3];
    float4 CamScalars;         // x F_L_n, y cz, z 1/cz, w A_w_J
    float4 ToneA;              // x n (peak nits), y g, z t_1, w s_2
    float4 ToneB;              // x u_2, y m_2, z forward limit, w 1/A_w_J
    float4 Shape;              // x limit J max, y 1/model gamma, z sat, w sat threshold
    float4 Shape2;             // x compr, y chroma compress scale, z mid J, w focus distance
    float4 Shape3;             // x lower hull 1/gamma, y peak / 100, z encoding (0 BT.1886, 1 PQ), w unused
    int4 SearchRange;          // x low, y high
};

Texture2D<float4> AcesTable : register(ACES_TABLE_REGISTER);

static const int AcesTableSize = 360;
static const int AcesBaseIndex = 1;
static const float AcesPi = 3.14159265358979;
static const float CamNlOffset = 27.13;
static const float SmoothCusps = 0.12;
static const float CuspMidBlend = 1.3;
static const float FocusGainBlend = 0.3;
static const float CompressionThreshold = 0.75;

// pow with its base held at zero or above: the reference's pow of a negative base is NaN, which
// no step relies on, and the compiler insists on knowing.
float SafePow(float b, float e) { return pow(max(b, 0.0), e); }
float3 SafePow(float3 b, float3 e) { return pow(max(b, 0.0), e); }

float3 RowMul(float3 v, float4 m[3])
{
    // A row vector times a matrix: v * M, as the CTL multiplies.
    return v.x * m[0].xyz + v.y * m[1].xyz + v.z * m[2].xyz;
}

float CtlCopySign(float x, float y)
{
    return y > 0.0 ? abs(x) : (y < 0.0 ? -abs(x) : 0.0);
}

float WrapTo360(float hue)
{
    float y = fmod(hue, 360.0);
    return y < 0.0 ? y + 360.0 : y;
}

int HuePosition(float hue, int size)
{
    return (int)(WrapTo360(hue) / 360.0 * size);
}

float4 Cusps(int i) { return AcesTable.Load(int3(i, 0, 0)); }
float ReachAt(int i) { return AcesTable.Load(int3(i, 1, 0)).x; }

// ---- CAM ----

float ConeFwdMagnitude(float rc)
{
    float f = SafePow(rc, 0.42);
    return f / (CamNlOffset + f);
}

float ConeInvMagnitude(float ra)
{
    float lim = min(ra, 0.99);
    float f = CamNlOffset * lim / (1.0 - lim);
    return SafePow(f, 1.0 / 0.42);
}

float ConeFwd(float v) { return CtlCopySign(ConeFwdMagnitude(abs(v)), v); }
float ConeInv(float v) { return CtlCopySign(ConeInvMagnitude(abs(v)), v); }

float AchromaticToJ(float a) { return 100.0 * SafePow(a, CamScalars.y); }
float JToAchromatic(float j) { return SafePow(j * 0.01, CamScalars.z); }

float JToY(float j)
{
    return ConeInvMagnitude(CamScalars.w * JToAchromatic(abs(j))) / CamScalars.x;
}

float YToJ(float y)
{
    float ra = ConeFwdMagnitude(abs(y) * CamScalars.x);
    return CtlCopySign(AchromaticToJ(ra * ToneB.w), y);
}

float3 RgbToAab(float3 rgb, float4 rgbToCam[3])
{
    float3 m = RowMul(rgb, rgbToCam);
    float3 a = float3(ConeFwd(m.x), ConeFwd(m.y), ConeFwd(m.z));
    return RowMul(a, ConeToAab);
}

float3 RgbToJMh(float3 rgb, float4 rgbToCam[3])
{
    float3 aab = RgbToAab(rgb, rgbToCam);
    if (aab.x <= 0.0)
    {
        return 0.0;
    }

    float j = AchromaticToJ(aab.x);
    float m = sqrt(aab.y * aab.y + aab.z * aab.z);
    float h = WrapTo360(atan2(aab.z, aab.y) * 180.0 / AcesPi);
    return float3(j, m, h);
}

float3 JMhToRgb(float3 jmh, float4 camToRgb[3])
{
    float hr = jmh.z / 180.0 * AcesPi;
    float3 aab = float3(JToAchromatic(jmh.x), jmh.y * cos(hr), jmh.y * sin(hr));
    float3 a = RowMul(aab, AabToCone);
    float3 m = float3(ConeInv(a.x), ConeInv(a.y), ConeInv(a.z));
    return RowMul(m, camToRgb);
}

// ---- Tone scale ----

float ToneFwd(float x)
{
    float f = ToneB.y * SafePow(max(0.0, x) / (x + ToneA.w), ToneA.y);
    float h = max(0.0, f * f / (f + ToneA.z));
    return h * 100.0;
}

float ToneInv(float y)
{
    float z = max(0.0, min(ToneA.x / (ToneB.x * 100.0), y));
    float h = (z + sqrt(z * (4.0 * ToneA.z + z))) / 2.0;
    return ToneA.w / (SafePow(ToneB.y / h, 1.0 / ToneA.y) - 1.0);
}

// ---- Chroma compression ----

float ReachM(float h)
{
    int base = HuePosition(h, AcesTableSize);
    float t = h - base;
    int lo = base + AcesBaseIndex;
    return lerp(ReachAt(lo), ReachAt(lo + 1), t);
}

float Toe(float x, float limit, float k1In, float k2In, bool invert)
{
    if (x > limit)
    {
        return x;
    }

    float k2 = max(k2In, 0.001);
    float k1 = sqrt(k1In * k1In + k2 * k2);
    float k3 = (limit + k1) / (limit + k2);
    if (invert)
    {
        return (x * x + k1 * x) / (k3 * (x + k2));
    }

    float minusB = k3 * x - k1;
    float minusC = k2 * k3 * x;
    return 0.5 * (minusB + sqrt(minusB * minusB + 4.0 * minusC));
}

float ChromaNorm(float h)
{
    float hr = h / 180.0 * AcesPi;
    float a = cos(hr);
    float b = sin(hr);
    float cos2 = a * a - b * b;
    float sin2 = 2.0 * a * b;
    float cos3 = 4.0 * a * a * a - 3.0 * a;
    float sin3 = 3.0 * b - 4.0 * b * b * b;
    float m = 11.34072 * a + 16.46899 * cos2 + 7.88380 * cos3 + 14.66441 * b - 6.37224 * sin2 + 9.19364 * sin3 + 77.12896;
    return m * Shape2.y;
}

float3 ChromaCompress(float3 jmh, float tonemappedJ, float originalJ, bool invert)
{
    float m = jmh.y;
    float h = jmh.z;
    if (m == 0.0)
    {
        return float3(invert ? originalJ : tonemappedJ, m, h);
    }

    float nJ = tonemappedJ / Shape.x;
    float snJ = max(0.0, 1.0 - nJ);
    float mNorm = ChromaNorm(h);
    float limit = SafePow(nJ, Shape.y) * ReachM(h) / mNorm;
    float toeLimit = limit - 0.001;
    float toeSnJSat = snJ * Shape.z;
    float toeSqrt = sqrt(nJ * nJ + Shape.w);
    float toeCompr = nJ * Shape2.x;

    if (!invert)
    {
        m = m * SafePow(tonemappedJ / originalJ, Shape.y) / mNorm;
        m = limit - Toe(limit - m, toeLimit, toeSnJSat, toeSqrt, false);
        m = Toe(m, limit, toeCompr, snJ, false) * mNorm;
        return float3(tonemappedJ, m, h);
    }

    m = m / mNorm;
    m = Toe(m, limit, toeCompr, snJ, true);
    m = limit - Toe(limit - m, toeLimit, toeSnJSat, toeSqrt, true);
    m = m * mNorm * SafePow(tonemappedJ / originalJ, -Shape.y);
    return float3(originalJ, m, h);
}

// ---- Gamut compression ----

float CompressionSlope(float intersectJ, float focusJ, float slopeGain)
{
    float direction = intersectJ < focusJ ? intersectJ : Shape.x - intersectJ;
    return direction * (intersectJ - focusJ) / (focusJ * slopeGain);
}

float SolveJIntersect(float j, float m, float focusJ, float slopeGain)
{
    float maxJ = Shape.x;
    float mScaled = m / slopeGain;
    float a = mScaled / focusJ;
    if (j < focusJ)
    {
        float b = 1.0 - mScaled;
        float c = -j;
        return -2.0 * c / (b + sqrt(b * b - 4.0 * a * c));
    }

    float b2 = -(1.0 + mScaled + maxJ * a);
    float c2 = maxJ * mScaled + j;
    return -2.0 * c2 / (b2 - sqrt(b2 * b2 - 4.0 * a * c2));
}

float SminScaled(float a, float b, float reference)
{
    float s = SmoothCusps * reference;
    float h = max(s - abs(a - b), 0.0) / s;
    return min(a, b) - h * h * h * s * (1.0 / 6.0);
}

float BoundaryIntersectionM(float jAxis, float slope, float invGamma, float jMax, float mMax, float jReference)
{
    // Held at zero or above, as the C# port does: a rounding error past J max would be NaN.
    float normalisedJ = max(0.0, jAxis / jReference);
    float shifted = jReference * SafePow(normalisedJ, invGamma);
    return shifted * mMax / (jMax - slope * mMax);
}

float GamutBoundaryM(float2 cusp, float gammaTopInv, float jSource, float slope, float jCusp)
{
    float jMax = Shape.x;
    float lower = BoundaryIntersectionM(jSource, slope, Shape3.x, cusp.x, cusp.y, jCusp);
    float upper = BoundaryIntersectionM(jMax - jSource, -slope, gammaTopInv, jMax - cusp.x, cusp.y, jMax - jCusp);
    return SminScaled(lower, upper, cusp.y);
}

float FocusGain(float j, float threshold)
{
    float gain = Shape.x * Shape2.w;
    if (j > threshold)
    {
        float adjust = log10((Shape.x - threshold) / max(0.0001, Shape.x - j));
        gain *= adjust * adjust + 1.0;
    }

    return gain;
}

float RemapM(float m, float gamutM, float reachM, bool invert)
{
    float proportion = max(gamutM / reachM, CompressionThreshold);
    float threshold = proportion * gamutM;
    if (m <= threshold || proportion >= 1.0)
    {
        return m;
    }

    float mOffset = m - threshold;
    float gamutOffset = gamutM - threshold;
    float reachOffset = reachM - threshold;
    float scale = reachOffset / ((reachOffset / gamutOffset) - 1.0);
    float nd = mOffset / scale;
    float remapped = invert ? (nd >= 1.0 ? scale : scale * -(nd / (nd - 1.0))) : scale * nd / (1.0 + nd);
    return threshold + remapped;
}

float2 CuspFromTable(float h)
{
    int low = 0;
    int high = AcesBaseIndex + AcesTableSize;
    int i = HuePosition(h, AcesTableSize) + AcesBaseIndex;

    [loop]
    while (low + 1 < high)
    {
        if (h > Cusps(i).z)
        {
            low = i;
        }
        else
        {
            high = i;
        }

        i = (low + high) >> 1;
    }

    float4 lo = Cusps(high - 1);
    float4 hi = Cusps(high);
    float t = (h - lo.z) / (hi.z - lo.z);
    return float2(lerp(lo.x, hi.x, t), lerp(lo.y, hi.y, t));
}

int LookupHueInterval(float h)
{
    int i = AcesBaseIndex + HuePosition(h, AcesTableSize + 2);
    int lo = max(AcesBaseIndex, i + SearchRange.x);
    int hi = min(AcesBaseIndex + AcesTableSize, i + SearchRange.y);

    [loop]
    while (lo + 1 < hi)
    {
        if (h > Cusps(i).z)
        {
            lo = i;
        }
        else
        {
            hi = i;
        }

        i = (lo + hi) >> 1;
    }

    return max(1, hi);
}

float3 CompressGamut(float3 jmh, float jx, float2 cusp, float gammaTopInv, float focusJ, float threshold, bool invert)
{
    float slopeGain = FocusGain(jx, threshold);
    float jSource = SolveJIntersect(jmh.x, jmh.y, focusJ, slopeGain);
    float slope = CompressionSlope(jSource, focusJ, slopeGain);
    float jCusp = SolveJIntersect(cusp.x, cusp.y, focusJ, slopeGain);

    float gamutM = GamutBoundaryM(cusp, gammaTopInv, jSource, slope, jCusp);
    if (gamutM <= 0.0)
    {
        return float3(jmh.x, 0.0, jmh.z);
    }

    float reachM = BoundaryIntersectionM(jSource, slope, Shape.y, Shape.x, ReachM(jmh.z), Shape.x);
    float m = RemapM(jmh.y, gamutM, reachM, invert);
    return float3(jSource + m * slope, m, jmh.z);
}

float3 GamutCompress(float3 jmh, bool invert)
{
    if (jmh.x <= 0.0)
    {
        return float3(0.0, 0.0, jmh.z);
    }

    if (jmh.y < 0.0 || jmh.x > Shape.x)
    {
        return float3(jmh.x, 0.0, jmh.z);
    }

    int iHi = LookupHueInterval(jmh.z);
    float t = jmh.z - Cusps(iHi - 1).z;
    float2 cusp = CuspFromTable(jmh.z);
    float gammaTopInv = lerp(Cusps(iHi - 1).w, Cusps(iHi).w, t);
    float focusJ = lerp(cusp.x, Shape2.z, min(1.0, CuspMidBlend - (cusp.x / Shape.x)));
    float threshold = lerp(cusp.x, Shape.x, FocusGainBlend);

    float jx = jmh.x;
    if (invert && jx > threshold)
    {
        jx = CompressGamut(jmh, jx, cusp, gammaTopInv, focusJ, threshold, true).x;
    }

    return CompressGamut(jmh, jx, cusp, gammaTopInv, focusJ, threshold, invert);
}

// ---- Display encoding ----

float3 YToPq(float3 nits)
{
    const float m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
    float3 lm = SafePow(max(nits, 0.0) / 10000.0, m1);
    return SafePow((c1 + c2 * lm) / (1.0 + c3 * lm), m2);
}

float3 PqToY(float3 code)
{
    const float m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
    float3 np = SafePow(max(code, 0.0), 1.0 / m2);
    float3 l = max(np - c1, 0.0) / (c2 - c3 * np);
    return SafePow(l, 1.0 / m1) * 10000.0;
}

float3 DisplayEncode(float3 linearLight)
{
    linearLight = max(linearLight, 0.0);
    return Shape3.z > 0.5 ? YToPq(linearLight * 100.0) : SafePow(linearLight, 1.0 / 2.4);
}

float3 DisplayDecode(float3 code)
{
    return Shape3.z > 0.5 ? PqToY(code) / 100.0 : SafePow(max(code, 0.0), 2.4);
}

// ---- The transforms ----

// ACES2065-1 to limiting RGB, 1.0 being 100 nits.
float3 AcesOutputLinear(float3 aces)
{
    float3 ap1 = clamp(RowMul(aces, Ap0ToAp1), 0.0, ToneB.z);
    float3 clamped = RowMul(ap1, Ap1ToAp0);
    float3 jmh = RgbToJMh(clamped, InRgbToCam);

    float tonemappedJ = YToJ(ToneFwd(JToY(jmh.x) / 100.0));
    float3 toned = ChromaCompress(jmh, tonemappedJ, jmh.x, false);
    float3 compressed = GamutCompress(toned, false);
    return JMhToRgb(compressed, LimCamToRgb);
}

// ACES2065-1 to display code values: the whole preset.
float3 AcesOutput(float3 aces)
{
    float3 rgb = clamp(AcesOutputLinear(aces), 0.0, Shape3.y);
    return DisplayEncode(RowMul(rgb, LimitToDisplay));
}

// Display code values back to ACES2065-1: the inverse preset.
float3 AcesOutputInverse(float3 code)
{
    float3 rgb = clamp(RowMul(DisplayDecode(code), DisplayToLimit), 0.0, Shape3.y);
    float3 jmh = RgbToJMh(rgb, LimRgbToCam);
    float3 toned = GamutCompress(jmh, true);

    float linearLight = ToneInv(JToY(toned.x) / 100.0);
    float j = YToJ(linearLight * 100.0);
    float3 back = ChromaCompress(toned, toned.x, j, true);
    return JMhToRgb(back, InCamToRgb);
}

#endif
