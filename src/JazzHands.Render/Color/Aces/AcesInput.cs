namespace JazzHands.Render.Color.Aces;

/// <summary>What a clip's picture is, for ACES: which input transform brings it into ACES.</summary>
public enum InputTransform
{
    /// <summary>Picked from the stream: HDR PQ as <see cref="Rec2100Pq"/>, anything else as <see cref="Srgb"/>.</summary>
    Auto,

    /// <summary>sRGB-encoded Rec.709: screen captures, stills, graphics, most game footage.</summary>
    Srgb,

    /// <summary>A Rec.709 camera: the BT.709 OETF undone.</summary>
    Rec709,

    /// <summary>Scene-linear Rec.709 (an EXR render, say).</summary>
    LinearRec709,

    /// <summary>Sony S-Log3, S-Gamut3.</summary>
    SLog3,

    /// <summary>ARRI LogC3 at EI 800, ARRI Wide Gamut 3.</summary>
    LogC3,

    /// <summary>Panasonic V-Log, V-Gamut.</summary>
    VLog,

    /// <summary>A picture already rendered for an SDR display: the SDR output transform undone, so it comes out as it went in.</summary>
    SdrDisplay,

    /// <summary>HDR10 (BT.2100 PQ) already rendered for a 1000 nit display: the HDR output transform undone.</summary>
    Rec2100Pq,
}

/// <summary>
/// The ACES encodings and input transforms (Phase 44): ACEScct, ACEScg's primaries, and each
/// <see cref="InputTransform"/> from code values to ACES2065-1, by the published formulas. The
/// shader versions are in Aces.hlsl; <c>AcesReferenceTests</c> checks these against OpenColorIO.
/// </summary>
public static class AcesInput
{
    private const double CctA = 10.5402377416545;
    private const double CctB = 0.0729055341958355;
    private const double CctCutLinear = 0.0078125;
    private const double CctCutLog = 0.155251141552511;

    /// <summary>ACES2065-1 (AP0) to ACEScg (AP1), no adaptation: they share a white.</summary>
    public static M33 Ap0ToAp1 { get; } = Chromaticities.Ap0.RgbToXyz() * Chromaticities.Ap1.XyzToRgb();

    /// <summary>ACEScg (AP1) to ACES2065-1 (AP0).</summary>
    public static M33 Ap1ToAp0 { get; } = Chromaticities.Ap1.RgbToXyz() * Chromaticities.Ap0.XyzToRgb();

    /// <summary>Sony S-Gamut3 to AP0, as published.</summary>
    public static M33 SGamut3ToAp0 { get; } = new(
        new D3(0.7529826164, 0.0217076968, -0.0094160531),
        new D3(0.1433702111, 1.0153188705, 0.0033704177),
        new D3(0.1036471874, -0.0370265320, 1.0060455799));

    /// <summary>ARRI Wide Gamut 3 to AP0, as published.</summary>
    public static M33 ArriWideGamut3ToAp0 { get; } = new(
        new D3(0.6802055240, 0.0854149833, 0.0020565216),
        new D3(0.2361366004, 1.0174708366, -0.0625625029),
        new D3(0.0836578906, -0.1028858572, 1.0605059862));

    /// <summary>Panasonic V-Gamut to AP0, as published.</summary>
    public static M33 VGamutToAp0 { get; } = new(
        new D3(0.7246167064, 0.0213902462, -0.0092355628),
        new D3(0.1669152826, 0.9849081635, -0.0010569056),
        new D3(0.1084680110, -0.0062984009, 1.0102924109));

    /// <summary>Linear to ACEScct.</summary>
    public static double ToAcescct(double linear) =>
        linear <= CctCutLinear ? (CctA * linear) + CctB : (Math.Log2(linear) + 9.72) / 17.52;

    /// <summary>ACEScct to linear.</summary>
    public static double FromAcescct(double cct) =>
        cct <= CctCutLog ? (cct - CctB) / CctA
        : cct < (Math.Log2(65504.0) + 9.72) / 17.52 ? Math.Pow(2.0, (cct * 17.52) - 9.72)
        : 65504.0;

    /// <summary>ACES2065-1 to ACEScct (AP1 primaries, log encoded).</summary>
    public static D3 ToAcescct(D3 aces) => (aces * Ap0ToAp1).Map(ToAcescct);

    /// <summary>ACEScct to ACES2065-1.</summary>
    public static D3 FromAcescct(D3 cct) => cct.Map(FromAcescct) * Ap1ToAp0;

    /// <summary>The primaries an input transform's linear values are in, and the matrix from them to AP0.</summary>
    /// <remarks>
    /// The cameras' matrices are the ones their ACES input transforms publish (as OpenColorIO's
    /// ACES 2.0 studio config has them), not a Bradford adaptation of their primaries: the makers
    /// adapt their own way. Rec.709's is Bradford from D65 to the ACES white.
    /// </remarks>
    public static M33 ToAp0(InputTransform input) => input switch
    {
        InputTransform.SLog3 => SGamut3ToAp0,
        InputTransform.LogC3 => ArriWideGamut3ToAp0,
        InputTransform.VLog => VGamutToAp0,
        _ => Chromaticities.Rec709.To(Chromaticities.Ap0),
    };

    /// <summary>Code values, 0 to 1 full range, to ACES2065-1.</summary>
    public static D3 ToAces(D3 code, InputTransform input) => input switch
    {
        InputTransform.SdrDisplay => AcesOutputTransform.Rec709.Inverse(code),
        InputTransform.Rec2100Pq => AcesOutputTransform.Hdr10.Inverse(code),
        _ => code.Map(value => Decode(value, input)) * ToAp0(input),
    };

    /// <summary>
    /// The BT.709 camera curve undone, as ACES defines it: a gamma of 1/0.45 with an offset of 0.099
    /// and the straight segment that meets it smoothly (a slope of about 4.514, not the rounded 4.5).
    /// </summary>
    public static double Rec709Camera(double x)
    {
        const double gamma = 1.0 / 0.45, offset = 0.099;
        const double breakCode = offset / (gamma - 1.0);
        double breakLinear = Math.Pow((breakCode + offset) / (1.0 + offset), gamma);
        return x < breakCode ? x * breakLinear / breakCode : Math.Pow((x + offset) / (1.0 + offset), gamma);
    }

    /// <summary>One code value to scene-linear light, by an input transform's curve.</summary>
    public static double Decode(double x, InputTransform input) => input switch
    {
        InputTransform.Srgb or InputTransform.Auto => x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4),
        InputTransform.Rec709 => Rec709Camera(x),
        InputTransform.LinearRec709 => x,
        InputTransform.SLog3 => x >= 171.2102946929 / 1023.0
            ? (Math.Pow(10.0, ((x * 1023.0) - 420.0) / 261.5) * (0.18 + 0.01)) - 0.01
            : ((x * 1023.0) - 95.0) * 0.01125 / (171.2102946929 - 95.0),
        InputTransform.LogC3 => x > (5.367655 * 0.010591) + 0.092809
            ? (Math.Pow(10.0, (x - 0.385537) / 0.247190) - 0.052272) / 5.555556
            : (x - 0.092809) / 5.367655,
        InputTransform.VLog => x < 0.181 ? (x - 0.125) / 5.6 : Math.Pow(10.0, (x - 0.598206) / 0.241514) - 0.00873,
        _ => throw new ArgumentOutOfRangeException(nameof(input), input, "That transform has no per-channel curve."),
    };
}
