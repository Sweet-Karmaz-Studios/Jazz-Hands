using System.Numerics;

namespace JazzHands.Render.Color;

/// <summary>The transfer function a source is encoded with.</summary>
public enum TransferFunction
{
    /// <summary>BT.709, which covers almost all SDR video.</summary>
    Bt709 = 0,

    /// <summary>SMPTE ST 2084, PQ. HDR10.</summary>
    Pq = 1,

    /// <summary>ARIB STD-B67, HLG. Broadcast HDR and most phone HDR.</summary>
    Hlg = 2,

    /// <summary>Already linear; used by EXR and by generated sources.</summary>
    Linear = 3,

    /// <summary>IEC 61966-2-1, for PNG, JPEG and other stills.</summary>
    Srgb = 4,
}

/// <summary>
/// The YUV to RGB conversion for one source: matrix, range and transfer function.
/// </summary>
/// <remarks>
/// Getting this wrong is the classic way footage ends up subtly washed out or too contrasty, and
/// the mistakes are invisible until you compare against the source. The coefficients come from
/// the specs rather than from a table someone typed; see the color-science skill.
/// </remarks>
public sealed record YuvColorSpace(Matrix4x4 Matrix, bool IsFullRange, TransferFunction Transfer, int BitDepth)
{
    /// <summary>BT.709 limited range, 8-bit. The default for HD video.</summary>
    public static readonly YuvColorSpace Bt709Limited = new(BuildMatrix(0.2126, 0.0722), false, TransferFunction.Bt709, 8);

    /// <summary>
    /// An RGB source, such as a still: no matrix and full range, just the transfer function it
    /// was encoded with.
    /// </summary>
    public static YuvColorSpace Rgb(TransferFunction transfer) => new(Matrix4x4.Identity, true, transfer, 8);

    /// <summary>The luma offset to subtract, as a fraction of full scale: 16/255 for 8-bit limited range, zero for full.</summary>
    public float LumaOffset => RangeFor(BitDepth).LumaOffset;

    /// <summary>The luma scale after offsetting: 255/219 for 8-bit limited range, one for full.</summary>
    public float LumaRange => RangeFor(BitDepth).LumaRange;

    /// <summary>The chroma offset to subtract: 128/255 at 8 bits, which is not quite a half.</summary>
    public float ChromaOffset => RangeFor(BitDepth).ChromaOffset;

    /// <summary>The chroma scale after offsetting: 255/224 for 8-bit limited range, one for full.</summary>
    public float ChromaRange => RangeFor(BitDepth).ChromaRange;

    /// <summary>
    /// The offsets and scales that turn normalised samples of a bit depth into Y in 0 to 1 and
    /// chroma in -0.5 to 0.5.
    /// </summary>
    /// <remarks>
    /// A UNORM sample is its code value over 2^n - 1, and every constant here is exact in those
    /// units. The shortcuts are each worth a code value: chroma 128 at 8 bits is 0.50196, not a
    /// half, and 10-bit black is 64/1023, not 16/255. Both lift R and B off neutral grey.
    /// </remarks>
    /// <param name="bitDepth">Bits per sample of the decoded frame, which is what the texture holds.</param>
    public SampleRange RangeFor(int bitDepth)
    {
        int bits = Math.Clamp(bitDepth, 8, 16);
        float scale = 1 << (bits - 8);
        float max = (1 << bits) - 1;
        float chromaOffset = 128.0f * scale / max;

        return IsFullRange
            ? new SampleRange(0.0f, 1.0f, chromaOffset, 1.0f)
            : new SampleRange(16.0f * scale / max, max / (219.0f * scale), chromaOffset, max / (224.0f * scale));
    }

    /// <summary>
    /// Corrects for MSB-aligned 10-bit samples. P010 stores ten bits in the top of a sixteen bit
    /// word, so a UNORM sample comes back as value/65535 where the code expects value/1023.
    /// </summary>
    public float LumaScale => BitDepth switch
    {
        10 => 65535.0f / 65472.0f,
        12 => 65535.0f / 65520.0f,
        _ => 1.0f,
    };

    /// <summary>
    /// Builds the conversion for a source, from the colour signalling the probe or decoder read.
    /// </summary>
    /// <param name="matrixName">FFmpeg's colour space name, for example bt709 or bt2020nc.</param>
    /// <param name="transferName">FFmpeg's transfer name, for example bt709 or smpte2084.</param>
    /// <param name="isFullRange">True for full-swing luma.</param>
    /// <param name="bitDepth">Bits per component of the decoded frame.</param>
    public static YuvColorSpace From(string matrixName, string transferName, bool isFullRange, int bitDepth)
    {
        ArgumentNullException.ThrowIfNull(matrixName);
        ArgumentNullException.ThrowIfNull(transferName);

        // Luma coefficients for red and blue; green is whatever is left.
        Matrix4x4 matrix = matrixName.ToLowerInvariant() switch
        {
            "bt470bg" or "smpte170m" or "bt601" => BuildMatrix(0.299, 0.114),
            "bt2020nc" or "bt2020_ncl" or "bt2020c" or "bt2020_cl" => BuildMatrix(0.2627, 0.0593),
            _ => BuildMatrix(0.2126, 0.0722),
        };

        TransferFunction transfer = transferName.ToLowerInvariant() switch
        {
            "smpte2084" => TransferFunction.Pq,
            "arib-std-b67" => TransferFunction.Hlg,
            "linear" => TransferFunction.Linear,
            "iec61966-2-1" or "srgb" => TransferFunction.Srgb,
            _ => TransferFunction.Bt709,
        };

        return new YuvColorSpace(matrix, isFullRange, transfer, bitDepth);
    }

    /// <summary>
    /// The inverse of the RGB to YUV matrix for the given luma coefficients. Derived rather than
    /// tabulated, because a typo in a table is invisible until someone compares to the source.
    /// </summary>
    private static Matrix4x4 BuildMatrix(double kr, double kb)
    {
        double kg = 1.0 - kr - kb;

        // R = Y + 2(1-Kr)Cr
        // B = Y + 2(1-Kb)Cb
        // G = Y - 2(1-Kb)(Kb/Kg)Cb - 2(1-Kr)(Kr/Kg)Cr
        double rCr = 2.0 * (1.0 - kr);
        double bCb = 2.0 * (1.0 - kb);
        double gCb = -2.0 * (1.0 - kb) * kb / kg;
        double gCr = -2.0 * (1.0 - kr) * kr / kg;

        return new Matrix4x4(
            1.0f, 0.0f, (float)rCr, 0.0f,
            1.0f, (float)gCb, (float)gCr, 0.0f,
            1.0f, (float)bCb, 0.0f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);
    }
}

/// <summary>What turns normalised YUV samples into Y in 0 to 1 and chroma about zero.</summary>
/// <param name="LumaOffset">Subtracted from luma.</param>
/// <param name="LumaRange">Multiplies luma after the offset.</param>
/// <param name="ChromaOffset">Subtracted from each chroma sample.</param>
/// <param name="ChromaRange">Multiplies chroma after the offset.</param>
public readonly record struct SampleRange(float LumaOffset, float LumaRange, float ChromaOffset, float ChromaRange);
