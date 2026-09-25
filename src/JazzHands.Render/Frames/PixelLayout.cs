using System.Collections.Immutable;
using Vortice.DXGI;

namespace JazzHands.Render.Frames;

/// <summary>One plane of a pixel layout.</summary>
/// <param name="Format">The texture format the plane is uploaded as.</param>
/// <param name="WidthShift">How far the plane's width is halved: 0 for full, 1 for half.</param>
/// <param name="HeightShift">How far the plane's height is halved.</param>
/// <param name="Channels">Samples per texel: 1 for a single plane, 2 for interleaved chroma.</param>
public sealed record PixelPlane(Format Format, int WidthShift, int HeightShift, int Channels)
{
    /// <summary>This plane's width for a given frame width.</summary>
    public int WidthFor(int frameWidth) => Math.Max(1, (frameWidth + (1 << WidthShift) - 1) >> WidthShift);

    /// <summary>This plane's height for a given frame height.</summary>
    public int HeightFor(int frameHeight) => Math.Max(1, (frameHeight + (1 << HeightShift) - 1) >> HeightShift);

    /// <summary>Bytes in one sample of one channel: one, two or four.</summary>
    public int SampleBytes => Format switch
    {
        Format.R16_UNorm or Format.R16G16_UNorm or Format.R16G16B16A16_UNorm => 2,
        Format.R32_Float => 4,
        _ => 1,
    };

    /// <summary>Bytes in one row of this plane at a frame width.</summary>
    public int RowBytes(int frameWidth) => WidthFor(frameWidth) * Channels * SampleBytes;
}

/// <summary>
/// How a decoded frame's samples are arranged, in terms the render layer can act on.
/// </summary>
/// <remarks>
/// The decoder speaks in FFmpeg pixel formats and the compositor speaks in DXGI ones, and neither
/// project can see the other's vocabulary: Render knows nothing about FFmpeg by design. This is
/// the shared description they meet in. The engine, which sees both, maps one to the other.
///
/// Two families arrive. A hardware decoder produces semi-planar NV12 or P010, where chroma is one
/// plane of interleaved pairs. A software decoder produces planar YUV, where U and V are separate
/// planes. Both become textures and a layout saying what they are, and the compositor's source
/// pass reads either.
/// </remarks>
public sealed record PixelLayout(string Name, ImmutableArray<PixelPlane> Planes, int BitDepth)
{
    /// <summary>8-bit semi-planar 4:2:0, what an 8-bit hardware decoder produces.</summary>
    public static readonly PixelLayout Nv12 = new(
        "nv12",
        [
            new PixelPlane(Format.R8_UNorm, 0, 0, 1),
            new PixelPlane(Format.R8G8_UNorm, 1, 1, 2),
        ],
        8)
    {
        PackedFormat = Format.NV12,
    };

    /// <summary>10-bit semi-planar 4:2:0, what a 10-bit hardware decoder produces.</summary>
    public static readonly PixelLayout P010 = new(
        "p010",
        [
            new PixelPlane(Format.R16_UNorm, 0, 0, 1),
            new PixelPlane(Format.R16G16_UNorm, 1, 1, 2),
        ],
        10)
    {
        PackedFormat = Format.P010,
    };

    /// <summary>8-bit planar 4:2:0, the software decoder's usual output.</summary>
    public static readonly PixelLayout Yuv420P = Planar("yuv420p", Format.R8_UNorm, 1, 1, 8);

    /// <summary>10-bit planar 4:2:0.</summary>
    public static readonly PixelLayout Yuv420P10 = Planar("yuv420p10le", Format.R16_UNorm, 1, 1, 10);

    /// <summary>10-bit planar 4:2:2, which ProRes and broadcast sources use.</summary>
    public static readonly PixelLayout Yuv422P10 = Planar("yuv422p10le", Format.R16_UNorm, 1, 0, 10);

    /// <summary>8-bit planar 4:4:4.</summary>
    public static readonly PixelLayout Yuv444P = Planar("yuv444p", Format.R8_UNorm, 0, 0, 8);

    /// <summary>10-bit planar 4:4:4.</summary>
    public static readonly PixelLayout Yuv444P10 = Planar("yuv444p10le", Format.R16_UNorm, 0, 0, 10);

    /// <summary>8-bit planar 4:2:2.</summary>
    public static readonly PixelLayout Yuv422P = Planar("yuv422p", Format.R8_UNorm, 1, 0, 8);

    /// <summary>12-bit planar 4:2:0, which some camera intermediates use.</summary>
    public static readonly PixelLayout Yuv420P12 = Planar("yuv420p12le", Format.R16_UNorm, 1, 1, 12);

    /// <summary>8-bit planar 4:2:0 with alpha, what VP9 with alpha decodes to.</summary>
    public static readonly PixelLayout Yuva420P = PlanarAlpha("yuva420p", Format.R8_UNorm, 1, 1, 8);

    /// <summary>8-bit planar 4:4:4 with alpha.</summary>
    public static readonly PixelLayout Yuva444P = PlanarAlpha("yuva444p", Format.R8_UNorm, 0, 0, 8);

    /// <summary>10-bit planar 4:4:4 with alpha, what ProRes 4444 decodes to.</summary>
    public static readonly PixelLayout Yuva444P10 = PlanarAlpha("yuva444p10le", Format.R16_UNorm, 0, 0, 10);

    /// <summary>12-bit planar 4:4:4 with alpha, what ProRes 4444 XQ decodes to.</summary>
    public static readonly PixelLayout Yuva444P12 = PlanarAlpha("yuva444p12le", Format.R16_UNorm, 0, 0, 12);

    /// <summary>Eight bit interleaved RGBA, what the image decoder gives for an ordinary still.</summary>
    public static readonly PixelLayout Rgba = new("rgba", [new PixelPlane(Format.R8G8B8A8_UNorm, 0, 0, 4)], 8);

    /// <summary>Sixteen bit interleaved RGBA, for sixteen bit PNG and TIFF.</summary>
    public static readonly PixelLayout Rgba64 = new("rgba64le", [new PixelPlane(Format.R16G16B16A16_UNorm, 0, 0, 4)], 16);

    /// <summary>
    /// Planar float in G, B, R, A order, for EXR. Planar because swscale has no packed float
    /// output, and in that order because it is FFmpeg's.
    /// </summary>
    public static readonly PixelLayout Gbrapf32 = new(
        "gbrapf32le",
        [
            new PixelPlane(Format.R32_Float, 0, 0, 1),
            new PixelPlane(Format.R32_Float, 0, 0, 1),
            new PixelPlane(Format.R32_Float, 0, 0, 1),
            new PixelPlane(Format.R32_Float, 0, 0, 1),
        ],
        32);

    /// <summary>Every layout this build knows, by the FFmpeg name it answers to.</summary>
    public static readonly ImmutableArray<PixelLayout> All =
    [
        Nv12, P010, Yuv420P, Yuv420P10, Yuv420P12, Yuv422P, Yuv422P10, Yuv444P, Yuv444P10, Yuva420P, Yuva444P, Yuva444P10, Yuva444P12, Rgba, Rgba64, Gbrapf32,
    ];

    /// <summary>
    /// The one texture format that holds every plane, for the layouts a hardware decoder writes, or
    /// null when the planes can only be separate textures.
    /// </summary>
    /// <remarks>
    /// Direct3D 11 copies a video surface only into a texture of the same format: an NV12 plane
    /// cannot be copied into an R8 texture, and the call fails without a word. A copy target for
    /// these layouts is therefore one texture in the decoder's own format, and its planes are read
    /// through views in the plane formats above.
    /// </remarks>
    public Format? PackedFormat { get; init; }

    /// <summary>How many textures a frame in this layout needs.</summary>
    public int PlaneCount => Planes.Length;

    /// <summary>True when the samples are RGB rather than YUV.</summary>
    public bool IsRgb => Name is "rgba" or "rgba64le" or "gbrapf32le";

    /// <summary>True for planar YUV with a fourth plane of straight alpha.</summary>
    public bool IsYuva => Name.StartsWith("yuva", StringComparison.Ordinal);

    /// <summary>
    /// True when chroma arrives as one plane of interleaved pairs, which is what a hardware
    /// decoder gives and what the compositor samples with two channels rather than three planes.
    /// </summary>
    public bool IsSemiPlanar => Planes.Length == 2;

    /// <summary>Bytes one frame of this layout occupies at a size, which is what a cache budgets by.</summary>
    public long BytesFor(int width, int height)
    {
        long total = 0;

        foreach (PixelPlane plane in Planes)
        {
            long samples = (long)plane.WidthFor(width) * plane.HeightFor(height) * plane.Channels;
            total += samples * plane.SampleBytes;
        }

        return total;
    }

    /// <summary>The layout an FFmpeg pixel format name maps to, or null when nothing here matches.</summary>
    /// <remarks>
    /// By name rather than by value, because the value belongs to a library Render cannot see.
    /// The engine passes what the decoder said the format was.
    /// </remarks>
    public static PixelLayout? ForName(string? ffmpegName)
    {
        if (string.IsNullOrEmpty(ffmpegName))
        {
            return null;
        }

        foreach (PixelLayout layout in All)
        {
            if (string.Equals(layout.Name, ffmpegName, StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        return null;
    }

    private static PixelLayout PlanarAlpha(string name, Format format, int widthShift, int heightShift, int bitDepth) =>
        new(
            name,
            [
                new PixelPlane(format, 0, 0, 1),
                new PixelPlane(format, widthShift, heightShift, 1),
                new PixelPlane(format, widthShift, heightShift, 1),
                new PixelPlane(format, 0, 0, 1),
            ],
            bitDepth);

    private static PixelLayout Planar(string name, Format format, int widthShift, int heightShift, int bitDepth) =>
        new(
            name,
            [
                new PixelPlane(format, 0, 0, 1),
                new PixelPlane(format, widthShift, heightShift, 1),
                new PixelPlane(format, widthShift, heightShift, 1),
            ],
            bitDepth);
}
