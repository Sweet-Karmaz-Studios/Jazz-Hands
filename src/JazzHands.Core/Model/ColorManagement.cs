namespace JazzHands.Core.Model;

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

/// <summary>How a project handles colour (Phase 44).</summary>
public enum ColorPipeline
{
    /// <summary>Display referred, as every project before Phase 44: pictures blend in linear BT.709 and show as they are.</summary>
    DisplayReferred,

    /// <summary>ACES: every picture brought into ACEScg by its input transform, graded in ACEScct, and shown through the ACES 2.0 output transform.</summary>
    Aces,
}

/// <summary>What an ACES project is rendered for.</summary>
public enum AcesOutput
{
    /// <summary>SDR video: 100 nits, Rec.709, BT.1886.</summary>
    Rec709,

    /// <summary>HDR10: 1000 nits, P3-D65 limited, in BT.2100 PQ.</summary>
    Hdr10,
}

/// <summary>A project's colour management (Phase 44). Absent, a project is display referred, as it always was.</summary>
/// <param name="Pipeline">Display referred, or ACES.</param>
/// <param name="Output">What an ACES project is rendered for.</param>
public sealed record ColorManagement(ColorPipeline Pipeline = ColorPipeline.DisplayReferred, AcesOutput Output = AcesOutput.Rec709) : IEquatable<ColorManagement>
{
    /// <summary>True for ACES.</summary>
    public bool IsAces => Pipeline == ColorPipeline.Aces;
}
