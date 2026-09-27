namespace JazzHands.Render.Color.Aces;

/// <summary>How an output transform's display values are encoded.</summary>
public enum DisplayEncoding
{
    /// <summary>BT.1886, gamma 2.4 with a zero black: SDR video.</summary>
    Bt1886,

    /// <summary>SMPTE ST 2084 (PQ), absolute: HDR10.</summary>
    Pq,
}

/// <summary>
/// The ACES 2.0 output transform (Phase 44): scene-referred ACES2065-1 to display code values, and
/// back. A line by line port of the Academy's reference implementation (aces-core,
/// Lib.Academy.OutputTransform.ctl, Tonescale.ctl and DisplayEncoding.ctl, Apache 2.0, commit
/// 069b0bc), in doubles, checked against OpenColorIO's own implementation in
/// <c>AcesReferenceTests</c>.
/// </summary>
/// <remarks>
/// Construction builds the hue-dependent tables (the reach gamut's M at the limiting J, the
/// limiting gamut's cusps and its upper hull gammas) the same way the CTL's <c>init_ODTParams</c>
/// does; they are what the shader reads (<see cref="Tables"/>). Two places where the CTL leaves
/// the reader to guess are taken as OpenColorIO takes them: <c>lookup_hue_interval</c>'s search
/// range is signed arithmetic (the CTL declares it unsigned and adds a negative offset), and
/// <c>build_hue_table</c> starts its "last index" at -1 (the CTL reads it before setting it).
/// </remarks>
public sealed class AcesOutputTransform
{
    /// <summary>The number of hue entries in each table.</summary>
    public const int TableSize = 360;

    /// <summary>The entries with the two that wrap the hues round.</summary>
    public const int TotalTableSize = TableSize + 2;

    private const int BaseIndex = 1;
    private const double HueLimit = 360.0;
    private const int CuspCornerCount = 6;
    private const int TotalCornerCount = CuspCornerCount + 2;
    private const int MaxSortedCorners = 2 * CuspCornerCount;
    private const double ReachCuspTolerance = 1e-3;
    private const double DisplayCuspTolerance = 1e-7;
    private const double GammaMinimum = 0.0;
    private const double GammaMaximum = 5.0;
    private const double GammaSearchStep = 0.4;
    private const double GammaAccuracy = 1e-5;

    /// <summary>The luminance display 1.0 means, in nits.</summary>
    public const double ReferenceLuminance = 100.0;

    private const double LA = 100.0;
    private const double Yb = 20.0;
    private static readonly D3 Surround = new(0.9, 0.59, 0.9);
    private const double JScale = 100.0;
    private const double CamNlOffset = 0.2713 * 100.0;
    private const double CamNlScale = 4.0 * 100.0;
    private static readonly double ModelGamma = Surround.Y * (1.48 + Math.Sqrt(Yb / ReferenceLuminance));

    private const double ChromaCompress = 2.4;
    private const double ChromaCompressFact = 3.3;
    private const double ChromaExpand = 1.3;
    private const double ChromaExpandFact = 0.69;
    private const double ChromaExpandThr = 0.5;

    private const double SmoothCusps = 0.12;
    private const double SmoothM = 0.27;
    private const double CuspMidBlend = 1.3;
    private const double FocusGainBlend = 0.3;
    private const double FocusDistance = 1.35;
    private const double FocusDistanceScaling = 1.75;
    private const double CompressionThreshold = 0.75;

    private static readonly double[] TestPositions = [0.01, 0.1, 0.5, 0.8, 0.99];

    /// <summary>Creates a transform for a display: its peak, the gamut it may fill, the primaries it is encoded in and its encoding.</summary>
    public AcesOutputTransform(double peakLuminance, Chromaticities limiting, Chromaticities encoding, DisplayEncoding eotf)
    {
        PeakLuminance = peakLuminance;
        Limiting = limiting;
        Encoding = eotf;
        LimitToDisplay = limiting.RgbToXyz() * encoding.XyzToRgb();
        DisplayToLimit = LimitToDisplay.Invert();

        Input = JMhParams.For(Chromaticities.Ap0);
        Reach = JMhParams.For(Chromaticities.Ap1);
        Limit = JMhParams.For(limiting);
        Tone = new Tonescale(peakLuminance);

        LimitJMax = YToJ(peakLuminance, Input);
        ModelGammaInv = 1.0 / ModelGamma;
        ReachM = MakeReachMTable(Reach, LimitJMax);

        Sat = Math.Max(0.2, ChromaExpand - (ChromaExpand * ChromaExpandFact * Tone.LogPeak));
        SatThr = ChromaExpandThr / peakLuminance;
        Compr = ChromaCompress + (ChromaCompress * ChromaCompressFact * Tone.LogPeak);
        ChromaCompressScale = Math.Pow(0.03379 * peakLuminance, 0.30596) - 0.45135;

        MidJ = YToJ(Tone.Ct * ReferenceLuminance, Input);
        FocusDist = FocusDistance + (FocusDistance * FocusDistanceScaling * Tone.LogPeak);
        LowerHullGammaInv = 1.0 / (1.14 + (0.07 * Tone.LogPeak));

        GamutCusps = MakeUniformHueGamutTable();
        Hues = [.. GamutCusps.Select(cusp => cusp.Z)];
        UpperHullGammaInv = MakeUpperHullGammaTable();
        HueSearchRange = DetermineHueLinearitySearchRange(Hues);
    }

    /// <summary>SDR: 100 nits, Rec.709, BT.1886.</summary>
    public static AcesOutputTransform Rec709 { get; } = new(100, Chromaticities.Rec709, Chromaticities.Rec709, DisplayEncoding.Bt1886);

    /// <summary>HDR10: 1000 nits, P3-D65 limited, in BT.2100 PQ.</summary>
    public static AcesOutputTransform Hdr10 { get; } = new(1000, Chromaticities.P3D65, Chromaticities.Rec2020, DisplayEncoding.Pq);

    /// <summary>The display's peak luminance, in nits.</summary>
    public double PeakLuminance { get; }

    /// <summary>The gamut the output may fill.</summary>
    public Chromaticities Limiting { get; }

    /// <summary>How the display values are encoded.</summary>
    public DisplayEncoding Encoding { get; }

    /// <summary>Limiting RGB to the display's encoding primaries.</summary>
    public M33 LimitToDisplay { get; }

    /// <summary>The display's encoding primaries back to limiting RGB.</summary>
    public M33 DisplayToLimit { get; }

    internal JMhParams Input { get; }

    internal JMhParams Reach { get; }

    internal JMhParams Limit { get; }

    internal Tonescale Tone { get; }

    internal double LimitJMax { get; }

    internal double ModelGammaInv { get; }

    internal double[] ReachM { get; }

    internal double Sat { get; }

    internal double SatThr { get; }

    internal double Compr { get; }

    internal double ChromaCompressScale { get; }

    internal double MidJ { get; }

    internal double FocusDist { get; }

    internal double LowerHullGammaInv { get; }

    internal D3[] GamutCusps { get; }

    internal double[] Hues { get; }

    internal double[] UpperHullGammaInv { get; }

    internal (int Low, int High) HueSearchRange { get; }

    /// <summary>ACES2065-1 to display code values, 0 to 1: the whole preset, clamp and encoding included.</summary>
    public D3 Forward(D3 aces)
    {
        D3 rgb = ForwardLinear(aces).Clamp(0.0, PeakLuminance / ReferenceLuminance);
        return Encode(rgb * LimitToDisplay);
    }

    /// <summary>Display code values back to ACES2065-1: the inverse preset, which first holds the light inside the limiting gamut and the peak.</summary>
    public D3 Inverse(D3 code)
    {
        D3 rgb = (Decode(code) * DisplayToLimit).Clamp(0.0, PeakLuminance / ReferenceLuminance);
        return InverseLinear(rgb);
    }

    /// <summary>ACES2065-1 to limiting RGB, 1.0 being 100 nits: the CTL's <c>outputTransform_fwd</c>.</summary>
    public D3 ForwardLinear(D3 aces)
    {
        D3 clamped = ClampAp0ToAp1(aces, 0.0, Tone.ForwardLimit);
        D3 jmh = RgbToJMh(clamped, Input);
        D3 toned = TonemapAndCompressFwd(jmh);
        D3 compressed = GamutCompressFwd(toned);
        return JMhToRgb(compressed, Limit);
    }

    /// <summary>Limiting RGB back to ACES2065-1: the CTL's <c>outputTransform_inv</c>.</summary>
    public D3 InverseLinear(D3 rgb)
    {
        D3 compressed = RgbToJMh(rgb, Limit);
        D3 toned = GamutCompressInv(compressed);
        D3 jmh = TonemapAndCompressInv(toned);
        return JMhToRgb(jmh, Input);
    }

    /// <summary>Display-linear light (1.0 is 100 nits) to code values.</summary>
    public D3 Encode(D3 linear) => linear.Map(value =>
    {
        value = Math.Max(0.0, value);
        return Encoding == DisplayEncoding.Pq ? YToSt2084(value * ReferenceLuminance) : Math.Pow(value, 1.0 / 2.4);
    });

    /// <summary>Code values to display-linear light (1.0 is 100 nits).</summary>
    public D3 Decode(D3 code) => code.Map(value =>
        Encoding == DisplayEncoding.Pq ? St2084ToY(value) / ReferenceLuminance : Math.Pow(Math.Max(value, 0.0), 2.4));

    /// <summary>Linear nits to PQ, 0 to 1.</summary>
    public static double YToSt2084(double nits)
    {
        const double m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
        double lm = Math.Pow(nits / 10000.0, m1);
        return Math.Pow((c1 + (c2 * lm)) / (1.0 + (c3 * lm)), m2);
    }

    /// <summary>PQ, 0 to 1, to linear nits.</summary>
    public static double St2084ToY(double code)
    {
        const double m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
        double np = Math.Pow(Math.Max(code, 0.0), 1.0 / m2);
        double l = Math.Max(np - c1, 0.0) / (c2 - (c3 * np));
        return Math.Pow(l, 1.0 / m1) * 10000.0;
    }

    // ---- CAM ----

    internal sealed record JMhParams(M33 RgbToCam16C, M33 Cam16CToRgb, M33 ConeResponseToAab, M33 AabToConeResponse, double FLN, double Cz, double InvCz, double AwJ, double InvAwJ)
    {
        public static JMhParams For(Chromaticities primaries)
        {
            var cam16 = new Chromaticities((0.8336, 0.1735), (2.3854, -1.4659), (0.087, -0.125), (0.333, 0.333));
            M33 matrix16 = cam16.XyzToRgb();
            var baseConeResponseToAab = new M33(new D3(2.0, 1.0, 1.0 / 9.0), new D3(1.0, -12.0 / 11.0, 1.0 / 9.0), new D3(1.0 / 20.0, 1.0 / 11.0, -2.0 / 9.0));

            M33 rgbToXyz = primaries.RgbToXyz();
            D3 xyzW = D3.All(ReferenceLuminance) * rgbToXyz;
            double yW = xyzW.Y;
            D3 rgbW = xyzW * matrix16;

            double k = 1.0 / ((5.0 * LA) + 1.0);
            double k4 = k * k * k * k;
            double fL = (0.2 * k4 * (5.0 * LA)) + (0.1 * Math.Pow(1.0 - k4, 2.0) * Math.Pow(5.0 * LA, 1.0 / 3.0));
            double fLN = fL / ReferenceLuminance;
            double cz = ModelGamma;

            var dRgb = new D3(fLN * yW / rgbW.X, fLN * yW / rgbW.Y, fLN * yW / rgbW.Z);
            var rgbWc = new D3(dRgb.X * rgbW.X, dRgb.Y * rgbW.Y, dRgb.Z * rgbW.Z);
            D3 rgbAw = rgbWc.Map(ConeCompressFwd);

            M33 coneResponseToAab = (CamNlScale * M33.Identity) * baseConeResponseToAab;
            double aW = (coneResponseToAab[0, 0] * rgbAw.X) + (coneResponseToAab[1, 0] * rgbAw.Y) + (coneResponseToAab[2, 0] * rgbAw.Z);
            double aWJ = ConeCompressFwdMagnitude(fL);

            M33 rgbToCam16 = rgbToXyz * matrix16 * (ReferenceLuminance * M33.Identity);
            M33 rgbToCam16C = rgbToCam16 * M33.Diagonal(dRgb);

            D3 Row(int row) => new(
                coneResponseToAab[row, 0] / aW,
                coneResponseToAab[row, 1] * 43.0 * Surround.Z,
                coneResponseToAab[row, 2] * 43.0 * Surround.Z);
            var matrixConeToAab = new M33(Row(0), Row(1), Row(2));

            return new JMhParams(rgbToCam16C, rgbToCam16C.Invert(), matrixConeToAab, matrixConeToAab.Invert(), fLN, cz, 1.0 / cz, aWJ, 1.0 / aWJ);
        }
    }

    private static double ConeCompressFwdMagnitude(double rc)
    {
        double fLY = Math.Pow(rc, 0.42);
        return fLY / (CamNlOffset + fLY);
    }

    private static double ConeCompressInvMagnitude(double ra)
    {
        double raLim = Math.Min(ra, 0.99);
        double fLY = CamNlOffset * raLim / (1.0 - raLim);
        return Math.Pow(fLY, 1.0 / 0.42);
    }

    private static double ConeCompressFwd(double v) => CopySign(ConeCompressFwdMagnitude(Math.Abs(v)), v);

    private static double ConeCompressInv(double v) => CopySign(ConeCompressInvMagnitude(Math.Abs(v)), v);

    /// <summary>The CTL's copysign: its sign function (1, -1, or 0 for zero and for NaN) times the magnitude.</summary>
    private static double CopySign(double x, double y) => y > 0.0 ? Math.Abs(x) : y < 0.0 ? -Math.Abs(x) : 0.0;

    private static double AchromaticToJ(double a, double cz) => JScale * Math.Pow(a, cz);

    private static double JToAchromatic(double j, double invCz) => Math.Pow(j * (1.0 / JScale), invCz);

    internal static double JToY(double j, JMhParams p)
    {
        double a = JToAchromatic(Math.Abs(j), p.InvCz);
        return ConeCompressInvMagnitude(p.AwJ * a) / p.FLN;
    }

    internal static double YToJ(double y, JMhParams p)
    {
        double ra = ConeCompressFwdMagnitude(Math.Abs(y) * p.FLN);
        return CopySign(AchromaticToJ(ra * p.InvAwJ, p.Cz), y);
    }

    internal static D3 RgbToAab(D3 rgb, JMhParams p)
    {
        D3 rgbM = rgb * p.RgbToCam16C;
        D3 rgbA = rgbM.Map(ConeCompressFwd);
        return rgbA * p.ConeResponseToAab;
    }

    private static D3 AabToJMh(D3 aab, JMhParams p)
    {
        if (aab.X <= 0.0)
        {
            return new D3(0, 0, 0);
        }

        double j = AchromaticToJ(aab.X, p.Cz);
        double m = Math.Sqrt((aab.Y * aab.Y) + (aab.Z * aab.Z));
        double h = WrapTo360(Math.Atan2(aab.Z, aab.Y) * 180.0 / Math.PI);
        return new D3(j, m, h);
    }

    internal static D3 RgbToJMh(D3 rgb, JMhParams p) => AabToJMh(RgbToAab(rgb, p), p);

    private static D3 JMhToAab(D3 jmh, JMhParams p)
    {
        double hRad = jmh.Z / 180.0 * Math.PI;
        return new D3(JToAchromatic(jmh.X, p.InvCz), jmh.Y * Math.Cos(hRad), jmh.Y * Math.Sin(hRad));
    }

    private static D3 AabToRgb(D3 aab, JMhParams p)
    {
        D3 rgbA = aab * p.AabToConeResponse;
        D3 rgbM = rgbA.Map(ConeCompressInv);
        return rgbM * p.Cam16CToRgb;
    }

    internal static D3 JMhToRgb(D3 jmh, JMhParams p) => AabToRgb(JMhToAab(jmh, p), p);

    private static D3 ClampAp0ToAp1(D3 aces, double low, double high)
    {
        M33 ap0ToAp1 = Chromaticities.Ap0.RgbToXyz() * Chromaticities.Ap1.XyzToRgb();
        M33 ap1ToAp0 = Chromaticities.Ap1.RgbToXyz() * Chromaticities.Ap0.XyzToRgb();
        return (aces * ap0ToAp1).Clamp(low, high) * ap1ToAp0;
    }

    // ---- Tables and lookups ----

    private static double WrapTo360(double hue)
    {
        double y = hue % 360.0;
        return y < 0.0 ? y + 360.0 : y;
    }

    private static int HuePositionInUniformTable(double hue, int tableSize) => (int)(WrapTo360(hue) / HueLimit * tableSize);

    private static double BaseHueForPosition(int index, int tableSize) => index * HueLimit / tableSize;

    private static double ReachMFromTable(double h, double[] table)
    {
        int baseIndex = HuePositionInUniformTable(h, TableSize);
        double t = h - baseIndex;
        int lo = baseIndex + BaseIndex;
        return Lerp(table[lo], table[lo + 1], t);
    }

    private static double Lerp(double a, double b, double t) => a + (t * (b - a));

    // ---- Chroma compression ----

    private static double ReinhardRemap(double scale, double nd, bool invert) =>
        invert ? (nd >= 1.0 ? scale : scale * -(nd / (nd - 1.0))) : scale * nd / (1.0 + nd);

    private static double Toe(double x, double limit, double k1In, double k2In, bool invert)
    {
        if (x > limit)
        {
            return x;
        }

        double k2 = Math.Max(k2In, 0.001);
        double k1 = Math.Sqrt((k1In * k1In) + (k2 * k2));
        double k3 = (limit + k1) / (limit + k2);

        if (invert)
        {
            return ((x * x) + (k1 * x)) / (k3 * (x + k2));
        }

        double minusB = (k3 * x) - k1;
        double minusC = k2 * k3 * x;
        return 0.5 * (minusB + Math.Sqrt((minusB * minusB) + (4.0 * minusC)));
    }

    private static double ChromaCompressNorm(double h, double scale)
    {
        double hr = h / 180.0 * Math.PI;
        double a = Math.Cos(hr);
        double b = Math.Sin(hr);
        double cosHr2 = (a * a) - (b * b);
        double sinHr2 = 2.0 * a * b;
        double cosHr3 = (4.0 * a * a * a) - (3.0 * a);
        double sinHr3 = (3.0 * b) - (4.0 * b * b * b);
        double m = (11.34072 * a) + (16.46899 * cosHr2) + (7.88380 * cosHr3) + (14.66441 * b) + (-6.37224 * sinHr2) + (9.19364 * sinHr3) + 77.12896;
        return m * scale;
    }

    private D3 ChromaCompressFwd(D3 jmh, double tonemappedJ)
    {
        (double j, double m, double h) = (jmh.X, jmh.Y, jmh.Z);
        double mCompr = m;

        if (m != 0.0)
        {
            double nJ = tonemappedJ / LimitJMax;
            double snJ = Math.Max(0.0, 1.0 - nJ);
            double mNorm = ChromaCompressNorm(h, ChromaCompressScale);
            double limit = Math.Pow(nJ, ModelGammaInv) * ReachMFromTable(h, ReachM) / mNorm;

            double toeLimit = limit - 0.001;
            double toeSnJSat = snJ * Sat;
            double toeSqrtNJSatThr = Math.Sqrt((nJ * nJ) + SatThr);
            double toeNJCompr = nJ * Compr;

            mCompr = m * Math.Pow(tonemappedJ / j, ModelGammaInv);
            mCompr /= mNorm;
            mCompr = limit - Toe(limit - mCompr, toeLimit, toeSnJSat, toeSqrtNJSatThr, false);
            mCompr = Toe(mCompr, limit, toeNJCompr, snJ, false);
            mCompr *= mNorm;
        }

        return new D3(tonemappedJ, mCompr, h);
    }

    private D3 ChromaCompressInv(D3 jmh, double j)
    {
        (double tonemappedJ, double mCompr, double h) = (jmh.X, jmh.Y, jmh.Z);
        double m = mCompr;

        if (mCompr != 0.0)
        {
            double nJ = tonemappedJ / LimitJMax;
            double snJ = Math.Max(0.0, 1.0 - nJ);
            double mNorm = ChromaCompressNorm(h, ChromaCompressScale);
            double limit = Math.Pow(nJ, ModelGammaInv) * ReachMFromTable(h, ReachM) / mNorm;

            double toeLimit = limit - 0.001;
            double toeSnJSat = snJ * Sat;
            double toeSqrtNJSatThr = Math.Sqrt((nJ * nJ) + SatThr);
            double toeNJCompr = nJ * Compr;

            m = mCompr / mNorm;
            m = Toe(m, limit, toeNJCompr, snJ, true);
            m = limit - Toe(limit - m, toeLimit, toeSnJSat, toeSqrtNJSatThr, true);
            m *= mNorm;
            m *= Math.Pow(tonemappedJ / j, -ModelGammaInv);
        }

        return new D3(j, m, h);
    }

    private D3 TonemapAndCompressFwd(D3 jmh)
    {
        double linear = JToY(jmh.X, Input) / ReferenceLuminance;
        double tonemappedY = Tone.Forward(linear);
        double jTs = YToJ(tonemappedY, Input);
        return ChromaCompressFwd(jmh, jTs);
    }

    internal D3 TonemapAndCompressInv(D3 jmhTc)
    {
        double luminance = JToY(jmhTc.X, Input);
        double linear = Tone.Inverse(luminance / ReferenceLuminance);
        double j = YToJ(linear * ReferenceLuminance, Input);
        return ChromaCompressInv(jmhTc, j);
    }

    // ---- Gamut compression ----

    private static double ComputeCompressionVectorSlope(double intersectJ, double focusJ, double limitJMax, double slopeGain)
    {
        double directionScalar = intersectJ < focusJ ? intersectJ : limitJMax - intersectJ;
        return directionScalar * (intersectJ - focusJ) / (focusJ * slopeGain);
    }

    private static double SolveJIntersect(double j, double m, double focusJ, double maxJ, double slopeGain)
    {
        double mScaled = m / slopeGain;
        double a = mScaled / focusJ;

        if (j < focusJ)
        {
            double b = 1.0 - mScaled;
            double c = -j;
            double det = (b * b) - (4.0 * a * c);
            double root = Math.Sqrt(det);
            return -2.0 * c / (b + root);
        }
        else
        {
            double b = -(1.0 + mScaled + (maxJ * a));
            double c = (maxJ * mScaled) + j;
            double det = (b * b) - (4.0 * a * c);
            double root = Math.Sqrt(det);
            return -2.0 * c / (b - root);
        }
    }

    private static double SminScaled(double a, double b, double scaleReference)
    {
        double sScaled = SmoothCusps * scaleReference;
        double h = Math.Max(sScaled - Math.Abs(a - b), 0.0) / sScaled;
        return Math.Min(a, b) - (h * h * h * sScaled * (1.0 / 6.0));
    }

    private static double EstimateLineAndBoundaryIntersectionM(double jAxisIntersect, double slope, double invGamma, double jMax, double mMax, double jIntersectionReference)
    {
        // Held at zero or above: at the top of the gamut the intersection can land a rounding error
        // past J max, and a fractional power of that tiny negative would be NaN. Zero is what the
        // geometry means there (no colourfulness at white).
        double normalisedJ = Math.Max(0.0, jAxisIntersect / jIntersectionReference);
        double shiftedIntersection = jIntersectionReference * Math.Pow(normalisedJ, invGamma);
        return shiftedIntersection * mMax / (jMax - (slope * mMax));
    }

    private static double FindGamutBoundaryIntersection((double J, double M) cusp, double jMax, double gammaTopInv, double gammaBottomInv, double jIntersectSource, double slope, double jIntersectCusp)
    {
        double mBoundaryLower = EstimateLineAndBoundaryIntersectionM(jIntersectSource, slope, gammaBottomInv, cusp.J, cusp.M, jIntersectCusp);

        double fJIntersectCusp = jMax - jIntersectCusp;
        double fJIntersectSource = jMax - jIntersectSource;
        double fCuspJ = jMax - cusp.J;
        double mBoundaryUpper = EstimateLineAndBoundaryIntersectionM(fJIntersectSource, -slope, gammaTopInv, fCuspJ, cusp.M, fJIntersectCusp);

        return SminScaled(mBoundaryLower, mBoundaryUpper, cusp.M);
    }

    private static double GetFocusGain(double j, double analyticalThreshold, double limitJMax, double focusDist)
    {
        double gain = limitJMax * focusDist;
        if (j > analyticalThreshold)
        {
            double adjustment = Math.Log10((limitJMax - analyticalThreshold) / Math.Max(0.0001, limitJMax - j));
            adjustment = (adjustment * adjustment) + 1.0;
            gain *= adjustment;
        }

        return gain;
    }

    private static double RemapM(double m, double gamutBoundaryM, double reachBoundaryM, bool invert)
    {
        double boundaryRatio = gamutBoundaryM / reachBoundaryM;
        double proportion = Math.Max(boundaryRatio, CompressionThreshold);
        double threshold = proportion * gamutBoundaryM;

        if (m <= threshold || proportion >= 1.0)
        {
            return m;
        }

        double mOffset = m - threshold;
        double gamutOffset = gamutBoundaryM - threshold;
        double reachOffset = reachBoundaryM - threshold;
        double scale = reachOffset / ((reachOffset / gamutOffset) - 1.0);
        double nd = mOffset / scale;
        return threshold + ReinhardRemap(scale, nd, invert);
    }

    private readonly record struct HueParams((double J, double M) Cusp, double GammaBottomInv, double GammaTopInv, double FocusJ, double AnalyticalThreshold);

    private D3 CompressGamut(D3 jmh, double jx, HueParams hdp, bool invert)
    {
        (double j, double m, double h) = (jmh.X, jmh.Y, jmh.Z);

        double slopeGain = GetFocusGain(jx, hdp.AnalyticalThreshold, LimitJMax, FocusDist);
        double jIntersectSource = SolveJIntersect(j, m, hdp.FocusJ, LimitJMax, slopeGain);
        double gamutSlope = ComputeCompressionVectorSlope(jIntersectSource, hdp.FocusJ, LimitJMax, slopeGain);
        double jIntersectCusp = SolveJIntersect(hdp.Cusp.J, hdp.Cusp.M, hdp.FocusJ, LimitJMax, slopeGain);

        double gamutBoundaryM = FindGamutBoundaryIntersection(hdp.Cusp, LimitJMax, hdp.GammaTopInv, hdp.GammaBottomInv, jIntersectSource, gamutSlope, jIntersectCusp);
        if (gamutBoundaryM <= 0.0)
        {
            return new D3(j, 0.0, h);
        }

        double reachMaxM = ReachMFromTable(h, ReachM);
        double reachBoundaryM = EstimateLineAndBoundaryIntersectionM(jIntersectSource, gamutSlope, ModelGammaInv, LimitJMax, reachMaxM, LimitJMax);
        double remappedM = RemapM(m, gamutBoundaryM, reachBoundaryM, invert);
        return new D3(jIntersectSource + (remappedM * gamutSlope), remappedM, h);
    }

    private static (double J, double M) CuspFromTable(double h, D3[] table)
    {
        int lowI = 0;
        int highI = BaseIndex + TableSize;
        int i = HuePositionInUniformTable(h, TableSize) + BaseIndex;

        while (lowI + 1 < highI)
        {
            if (h > table[i].Z)
            {
                lowI = i;
            }
            else
            {
                highI = i;
            }

            i = (lowI + highI) / 2;
        }

        D3 lo = table[highI - 1];
        D3 hi = table[highI];
        double t = (h - lo.Z) / (hi.Z - lo.Z);
        return (Lerp(lo.X, hi.X, t), Lerp(lo.Y, hi.Y, t));
    }

    private int LookupHueInterval(double h)
    {
        int i = BaseIndex + HuePositionInUniformTable(h, TotalTableSize);
        int iLo = Math.Max(BaseIndex, i + HueSearchRange.Low);
        int iHi = Math.Min(BaseIndex + TableSize, i + HueSearchRange.High);

        while (iLo + 1 < iHi)
        {
            if (h > Hues[i])
            {
                iLo = i;
            }
            else
            {
                iHi = i;
            }

            i = (iLo + iHi) / 2;
        }

        return Math.Max(1, iHi);
    }

    private static double ComputeFocusJ(double cuspJ, double midJ, double limitJMax) =>
        Lerp(cuspJ, midJ, Math.Min(1.0, CuspMidBlend - (cuspJ / limitJMax)));

    private HueParams HueDependentParams(double hue)
    {
        int iHi = LookupHueInterval(hue);
        double t = hue - Hues[iHi - 1];
        (double J, double M) cusp = CuspFromTable(hue, GamutCusps);
        return new HueParams(
            cusp,
            LowerHullGammaInv,
            Lerp(UpperHullGammaInv[iHi - 1], UpperHullGammaInv[iHi], t),
            ComputeFocusJ(cusp.J, MidJ, LimitJMax),
            Lerp(cusp.J, LimitJMax, FocusGainBlend));
    }

    private D3 GamutCompressFwd(D3 jmh)
    {
        (double j, double m, double h) = (jmh.X, jmh.Y, jmh.Z);
        if (j <= 0.0)
        {
            return new D3(0, 0, h);
        }

        if (m < 0.0 || j > LimitJMax)
        {
            return new D3(j, 0, h);
        }

        return CompressGamut(jmh, j, HueDependentParams(h), invert: false);
    }

    internal D3 GamutCompressInv(D3 jmh)
    {
        (double j, double m, double h) = (jmh.X, jmh.Y, jmh.Z);
        if (j <= 0.0)
        {
            return new D3(0, 0, h);
        }

        if (m < 0.0 || j > LimitJMax)
        {
            return new D3(j, 0, h);
        }

        HueParams hdp = HueDependentParams(h);
        double jx = j;
        if (jx > hdp.AnalyticalThreshold)
        {
            jx = CompressGamut(jmh, jx, hdp, invert: true).X;
        }

        return CompressGamut(jmh, jx, hdp, invert: true);
    }

    // ---- Table building ----

    private static D3 UnitCubeCuspCorner(int corner) => new(
        ((corner + 1) % CuspCornerCount) < 3 ? 1 : 0,
        ((corner + 5) % CuspCornerCount) < 3 ? 1 : 0,
        ((corner + 3) % CuspCornerCount) < 3 ? 1 : 0);

    private (D3[] Rgb, D3[] JMh) LimitingCuspCorners()
    {
        var tempRgb = new D3[CuspCornerCount];
        var tempJMh = new D3[CuspCornerCount];
        int minIndex = 0;
        for (int i = 0; i < CuspCornerCount; i++)
        {
            tempRgb[i] = (PeakLuminance / ReferenceLuminance) * UnitCubeCuspCorner(i);
            tempJMh[i] = RgbToJMh(tempRgb[i], Limit);
            if (tempJMh[i].Z < tempJMh[minIndex].Z)
            {
                minIndex = i;
            }
        }

        var rgb = new D3[TotalCornerCount];
        var jmh = new D3[TotalCornerCount];
        for (int i = 0; i < CuspCornerCount; i++)
        {
            rgb[i + 1] = tempRgb[(i + minIndex) % CuspCornerCount];
            jmh[i + 1] = tempJMh[(i + minIndex) % CuspCornerCount];
        }

        rgb[0] = rgb[CuspCornerCount];
        rgb[CuspCornerCount + 1] = rgb[1];
        jmh[0] = jmh[CuspCornerCount] with { Z = jmh[CuspCornerCount].Z - HueLimit };
        jmh[CuspCornerCount + 1] = jmh[1] with { Z = jmh[1].Z + HueLimit };
        return (rgb, jmh);
    }

    private D3[] ReachCorners()
    {
        var temp = new D3[CuspCornerCount];
        double limitA = JToAchromatic(LimitJMax, Reach.InvCz);
        int minIndex = 0;
        for (int i = 0; i < CuspCornerCount; i++)
        {
            D3 vector = UnitCubeCuspCorner(i);
            double lower = 0.0;
            double upper = Tone.ForwardLimit;
            while ((upper - lower) > ReachCuspTolerance)
            {
                double test = (lower + upper) / 2.0;
                double a = RgbToAab(test * vector, Reach).X;
                if (a < limitA)
                {
                    lower = test;
                }
                else
                {
                    upper = test;
                }
            }

            temp[i] = RgbToJMh(upper * vector, Reach);
            if (temp[i].Z < temp[minIndex].Z)
            {
                minIndex = i;
            }
        }

        var corners = new D3[TotalCornerCount];
        for (int i = 0; i < CuspCornerCount; i++)
        {
            corners[i + 1] = temp[(i + minIndex) % CuspCornerCount];
        }

        corners[0] = corners[CuspCornerCount] with { Z = corners[CuspCornerCount].Z - HueLimit };
        corners[CuspCornerCount + 1] = corners[1] with { Z = corners[1].Z + HueLimit };
        return corners;
    }

    private static double[] ExtractSortedCubeHues(D3[] reach, D3[] limit)
    {
        var sorted = new double[MaxSortedCorners];
        int idx = 0, reachIdx = 1, limitIdx = 1;
        while (reachIdx < CuspCornerCount + 1 || limitIdx < CuspCornerCount + 1)
        {
            double reachHue = reach[reachIdx].Z;
            double limitHue = limit[limitIdx].Z;
            if (reachHue == limitHue)
            {
                sorted[idx] = reachHue;
                reachIdx++;
                limitIdx++;
            }
            else if (reachHue < limitHue)
            {
                sorted[idx] = reachHue;
                reachIdx++;
            }
            else
            {
                sorted[idx] = limitHue;
                limitIdx++;
            }

            idx++;
        }

        return sorted;
    }

    private static void SampleInterval(int samples, double lower, double upper, double[] table, int start)
    {
        double delta = (upper - lower) / samples;
        for (int i = 0; i < samples; i++)
        {
            table[start + i] = lower + (i * delta);
        }
    }

    /// <summary>The CTL's <c>round</c>: to the nearest integer, halves away from zero, by truncation.</summary>
    private static int CtlRound(double x) => (int)(x < 0.0 ? x - 0.5 : x + 0.5);

    private static double[] BuildHueTable(double[] sortedHues)
    {
        var table = new double[TotalTableSize];
        double idealSpacing = TableSize / HueLimit;
        var samplesCount = new int[(2 * CuspCornerCount) + 2];
        int lastIdx = -1;
        int minIndex = sortedHues[0] == 0.0 ? 0 : 1;

        for (int hueIdx = 0; hueIdx < MaxSortedCorners; hueIdx++)
        {
            int nominalIdx = Math.Min(Math.Max(CtlRound(sortedHues[hueIdx] * idealSpacing), minIndex), TableSize - 1);
            if (lastIdx == nominalIdx)
            {
                if (hueIdx > 1 && samplesCount[hueIdx - 2] != samplesCount[hueIdx - 1] - 1)
                {
                    samplesCount[hueIdx - 1]--;
                }
                else
                {
                    nominalIdx++;
                }
            }

            samplesCount[hueIdx] = Math.Min(nominalIdx, TableSize - 1);
            minIndex = nominalIdx;
            lastIdx = minIndex;
        }

        int total = 0;
        int index = 0;
        SampleInterval(samplesCount[index], 0.0, sortedHues[index], table, total + 1);
        total += samplesCount[index];
        for (index = 1; index < MaxSortedCorners; index++)
        {
            int samples = samplesCount[index] - samplesCount[index - 1];
            SampleInterval(samples, sortedHues[index - 1], sortedHues[index], table, total + 1);
            total += samples;
        }

        SampleInterval(TableSize - total, sortedHues[index - 1], HueLimit, table, total + 1);

        table[0] = table[BaseIndex + TableSize - 1] - HueLimit;
        table[BaseIndex + TableSize] = table[BaseIndex] + HueLimit;
        return table;
    }

    private (double J, double M) FindDisplayCuspForHue(double hue, D3[] rgbCorners, D3[] jmhCorners)
    {
        int upperCorner = 1;
        for (int i = upperCorner; i < TotalCornerCount; i++)
        {
            if (jmhCorners[i].Z > hue)
            {
                upperCorner = i;
                break;
            }
        }

        int lowerCorner = upperCorner - 1;
        if (jmhCorners[lowerCorner].Z == hue)
        {
            return (jmhCorners[lowerCorner].X, jmhCorners[lowerCorner].Y);
        }

        D3 cuspLower = rgbCorners[lowerCorner];
        D3 cuspUpper = rgbCorners[upperCorner];
        double lowerT = 0.0;
        double upperT = 1.0;
        while ((upperT - lowerT) > DisplayCuspTolerance)
        {
            double sampleT = (lowerT + upperT) / 2.0;
            D3 jmh = RgbToJMh(D3.Lerp(cuspLower, cuspUpper, sampleT), Limit);
            if (jmh.Z < jmhCorners[lowerCorner].Z)
            {
                upperT = sampleT;
            }
            else if (jmh.Z >= jmhCorners[upperCorner].Z)
            {
                lowerT = sampleT;
            }
            else if (jmh.Z > hue)
            {
                upperT = sampleT;
            }
            else
            {
                lowerT = sampleT;
            }
        }

        D3 final = RgbToJMh(D3.Lerp(cuspLower, cuspUpper, (lowerT + upperT) / 2.0), Limit);
        return (final.X, final.Y);
    }

    private D3[] MakeUniformHueGamutTable()
    {
        D3[] reachCorners = ReachCorners();
        (D3[] rgbCorners, D3[] jmhCorners) = LimitingCuspCorners();
        double[] hueTable = BuildHueTable(ExtractSortedCubeHues(reachCorners, jmhCorners));

        var table = new D3[TotalTableSize];
        for (int i = BaseIndex; i < TotalTableSize; i++)
        {
            double hue = hueTable[i];
            (double j, double m) = FindDisplayCuspForHue(hue, rgbCorners, jmhCorners);
            table[i] = new D3(j, m * (1.0 + (SmoothM * SmoothCusps)), hue);
        }

        table[0] = new D3(table[TableSize].X, table[TableSize].Y, hueTable[0]);
        table[BaseIndex + TableSize] = new D3(table[BaseIndex].X, table[BaseIndex].Y, hueTable[BaseIndex + TableSize]);
        return table;
    }

    private static double[] MakeReachMTable(JMhParams reach, double limitJMax)
    {
        var table = new double[TotalTableSize];
        for (int i = 0; i < TableSize; i++)
        {
            double hue = BaseHueForPosition(i, TableSize);
            const double searchRange = 50.0;
            const double searchMaximum = 1300.0;
            double low = 0.0;
            double high = low + searchRange;
            bool outside = false;

            while (!outside && high < searchMaximum)
            {
                outside = AnyBelowZero(JMhToRgb(new D3(limitJMax, high, hue), reach));
                if (!outside)
                {
                    low = high;
                    high += searchRange;
                }
            }

            while (high - low > 1e-2)
            {
                double sampleM = (high + low) / 2.0;
                outside = AnyBelowZero(JMhToRgb(new D3(limitJMax, sampleM, hue), reach));
                if (outside)
                {
                    high = sampleM;
                }
                else
                {
                    low = sampleM;
                }
            }

            table[i + BaseIndex] = high;
        }

        table[0] = table[TableSize];
        table[BaseIndex + TableSize] = table[BaseIndex];
        return table;

        static bool AnyBelowZero(D3 rgb) => rgb.X < 0.0 || rgb.Y < 0.0 || rgb.Z < 0.0;
    }

    private double[] MakeUpperHullGammaTable()
    {
        var table = new double[TotalTableSize];
        double luminanceLimit = PeakLuminance / ReferenceLuminance;

        for (int i = BaseIndex; i < BaseIndex + TableSize; i++)
        {
            double hue = GamutCusps[i].Z;
            (double J, double M) cusp = (GamutCusps[i].X, GamutCusps[i].Y);

            double analyticalThreshold = Lerp(cusp.J, LimitJMax, FocusGainBlend);
            double focusJ = ComputeFocusJ(cusp.J, MidJ, LimitJMax);
            var tests = new (double TestJ, double JIntersect, double Slope, double JCusp)[TestPositions.Length];
            for (int test = 0; test < TestPositions.Length; test++)
            {
                double testJ = Lerp(cusp.J, LimitJMax, TestPositions[test]);
                double slopeGain = GetFocusGain(testJ, analyticalThreshold, LimitJMax, FocusDist);
                double jIntersect = SolveJIntersect(testJ, cusp.M, focusJ, LimitJMax, slopeGain);
                double slope = ComputeCompressionVectorSlope(jIntersect, focusJ, LimitJMax, slopeGain);
                double jCusp = SolveJIntersect(cusp.J, cusp.M, focusJ, LimitJMax, slopeGain);
                tests[test] = (testJ, jIntersect, slope, jCusp);
            }

            bool Fits(double topGammaInv)
            {
                foreach ((double _, double jIntersect, double slope, double jCusp) in tests)
                {
                    double approxM = FindGamutBoundaryIntersection(cusp, LimitJMax, topGammaInv, LowerHullGammaInv, jIntersect, slope, jCusp);
                    double approxJ = jIntersect + (slope * approxM);
                    D3 rgb = JMhToRgb(new D3(approxJ, approxM, hue), Limit);
                    if (!(rgb.X > luminanceLimit || rgb.Y > luminanceLimit || rgb.Z > luminanceLimit))
                    {
                        return false;
                    }
                }

                return true;
            }

            double low = GammaMinimum;
            double high = low + GammaSearchStep;
            bool outside = false;
            while (!outside && high < GammaMaximum)
            {
                if (!Fits(1.0 / high))
                {
                    low = high;
                    high += GammaSearchStep;
                }
                else
                {
                    outside = true;
                }
            }

            while ((high - low) > GammaAccuracy)
            {
                double testGamma = (high + low) / 2.0;
                if (Fits(1.0 / testGamma))
                {
                    high = testGamma;
                }
                else
                {
                    low = testGamma;
                }
            }

            table[i] = 1.0 / high;
        }

        table[0] = table[TableSize];
        table[TableSize + BaseIndex] = table[BaseIndex];
        return table;
    }

    private static (int Low, int High) DetermineHueLinearitySearchRange(double[] hues)
    {
        int low = 0, high = 1;
        for (int i = BaseIndex; i < BaseIndex + TableSize; i++)
        {
            int delta = i - HuePositionInUniformTable(hues[i], TotalTableSize);
            low = Math.Min(low, delta);
            high = Math.Max(high, delta + 1);
        }

        return (low, high);
    }
}

/// <summary>The ACES 2.0 tone scale: scene linear to display luminance and back (Lib.Academy.Tonescale.ctl).</summary>
internal sealed class Tonescale
{
    public Tonescale(double peakLuminance)
    {
        double n = peakLuminance;
        const double nR = 100.0, g = 1.15, c = 0.18, cD = 10.013, wG = 0.14, t1 = 0.04, rHitMin = 128.0, rHitMax = 896.0;

        double rHit = rHitMin + ((rHitMax - rHitMin) * (Math.Log(n / nR) / Math.Log(10000.0 / 100.0)));
        double m0 = n / nR;
        double m1 = 0.5 * (m0 + Math.Sqrt(m0 * (m0 + (4.0 * t1))));
        double u = Math.Pow((rHit / m1) / ((rHit / m1) + 1), g);
        double m = m1 / u;
        double wI = Math.Log(n / 100.0) / Math.Log(2.0);
        double cT = cD / nR * (1.0 + (wI * wG));
        double gIp = 0.5 * (cT + Math.Sqrt(cT * (cT + (4.0 * t1))));
        double gIpp2 = -(m1 * Math.Pow(gIp / m, 1.0 / g)) / (Math.Pow(gIp / m, 1.0 / g) - 1.0);
        double w2 = c / gIpp2;

        N = n;
        NR = nR;
        G = g;
        T1 = t1;
        Ct = cT;
        S2 = w2 * m1;
        U2 = Math.Pow((rHit / m1) / ((rHit / m1) + w2), g);
        M2 = m1 / U2;
        ForwardLimit = 8.0 * rHit;
        InverseLimit = n / (U2 * nR);
        LogPeak = Math.Log10(n / nR);
    }

    public double N { get; }

    public double NR { get; }

    public double G { get; }

    public double T1 { get; }

    public double Ct { get; }

    public double S2 { get; }

    public double U2 { get; }

    public double M2 { get; }

    public double ForwardLimit { get; }

    public double InverseLimit { get; }

    public double LogPeak { get; }

    /// <summary>Scene linear to display luminance in nits (the CTL's <c>tonescale_fwd</c>).</summary>
    public double Forward(double x)
    {
        double f = M2 * Math.Pow(Math.Max(0.0, x) / (x + S2), G);
        double h = Math.Max(0.0, f * f / (f + T1));
        return h * NR;
    }

    /// <summary>Display luminance, 1.0 being 100 nits, back to scene linear (the CTL's <c>tonescale_inv</c>, which takes it normalised).</summary>
    public double Inverse(double y)
    {
        double z = Math.Max(0.0, Math.Min(N / (U2 * NR), y));
        double h = (z + Math.Sqrt(z * ((4.0 * T1) + z))) / 2.0;
        return S2 / (Math.Pow(M2 / h, 1.0 / G) - 1.0);
    }
}
