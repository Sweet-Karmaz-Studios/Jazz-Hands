namespace JazzHands.Core.Model;

/// <summary>
/// The colour signalling of a video stream, as the file states it: what its code values mean.
/// </summary>
/// <remarks>
/// Kept on the stream so the picture is converted the way the file says rather than guessed at
/// render time. Names are FFmpeg's (<c>bt709</c>, <c>bt2020</c>, <c>smpte2084</c>,
/// <c>arib-std-b67</c>, <c>bt2020nc</c>), which is what the probe reads and what a person
/// comparing with ffprobe sees. Projects saved before Phase 17 have none, and the renderer guesses
/// from <see cref="MediaStream.IsHdr"/> and the picture size as it did then.
/// </remarks>
/// <param name="Primaries">The colour primaries, for example bt709 or bt2020.</param>
/// <param name="Transfer">The transfer characteristic, for example bt709, smpte2084 (PQ) or arib-std-b67 (HLG).</param>
/// <param name="Matrix">The YUV to RGB matrix, for example bt709, bt470bg (BT.601) or bt2020nc.</param>
/// <param name="IsFullRange">True for full range code values, false for limited (studio) range.</param>
/// <param name="MasteringMaxNits">The mastering display's peak luminance, 0 when the file does not say.</param>
/// <param name="MaxContentLightLevel">MaxCLL, the brightest pixel in the content, in nits; 0 when the file does not say.</param>
public sealed record StreamColor(
    string Primaries,
    string Transfer,
    string Matrix,
    bool IsFullRange = false,
    double MasteringMaxNits = 0,
    int MaxContentLightLevel = 0)
{
    /// <summary>HD video when a file says nothing.</summary>
    public static readonly StreamColor Bt709 = new("bt709", "bt709", "bt709");

    /// <summary>True for PQ or HLG, which the picture is tone mapped from.</summary>
    public bool IsHdr =>
        Transfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)
        || Transfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The brightest the content is meant to get, in nits, for the tone mapper: MaxCLL when the file
    /// gives it, the mastering display's peak otherwise, and 1000 (the common HDR10 grade) when it
    /// gives neither. HLG is display-relative and always 1000.
    /// </summary>
    public double PeakNits =>
        Transfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase) ? 1000.0
        : MaxContentLightLevel > 0 ? MaxContentLightLevel
        : MasteringMaxNits > 0 ? MasteringMaxNits
        : 1000.0;
}

/// <summary>How HDR is brought down into SDR.</summary>
public enum ToneMapOperator
{
    /// <summary>ITU-R BT.2390's EETF: untouched up to a knee, a smooth roll-off to the peak. The default.</summary>
    Bt2390,

    /// <summary>John Hable's filmic curve (Uncharted 2): a gentle toe and a long shoulder, more contrast.</summary>
    Hable,

    /// <summary>Linear up to 0.3, then a Möbius curve to the peak: keeps the most of the image as it was.</summary>
    Mobius,

    /// <summary>No curve: everything over SDR white clips. For checking what the others did.</summary>
    Clip,
}

/// <summary>How a clip, or the project by default, is tone mapped from HDR.</summary>
/// <param name="Operator">The curve.</param>
/// <param name="PeakNits">
/// The source's peak in nits, overriding what the file says; null to use the file's (see
/// <see cref="StreamColor.PeakNits"/>). Only meaningful on a clip.
/// </param>
/// <param name="Desaturate">
/// How much highlights the curve compresses lose their colour, 0 to 1: bright saturated colours
/// otherwise come out of a tone map looking neon. 0.5 by default.
/// </param>
public sealed record ToneMapping(
    ToneMapOperator Operator = ToneMapOperator.Bt2390,
    double? PeakNits = null,
    double Desaturate = 0.5)
{
    /// <summary>BT.2390, the file's own peak, half desaturation.</summary>
    public static readonly ToneMapping Default = new();

    /// <summary>A clip's own settings, else the project's, else the default.</summary>
    public static ToneMapping Resolve(ToneMapping? clip, ToneMapping? project) =>
        clip ?? project ?? Default;
}
