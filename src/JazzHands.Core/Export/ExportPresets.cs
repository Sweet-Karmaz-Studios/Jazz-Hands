using System.Collections.Immutable;
using JazzHands.Core.Model;

namespace JazzHands.Core.Export;

/// <summary>A built-in export recipe.</summary>
/// <param name="Name">What it is called on the command line: youtube-1080p.</param>
/// <param name="Description">One line for help and the export dialog.</param>
/// <param name="Container">The FFmpeg muxer.</param>
/// <param name="Extension">The file extension the container goes with.</param>
/// <param name="Codec">h264 or hevc.</param>
/// <param name="Encoders">FFmpeg encoders in the order to try them: the GPU first.</param>
/// <param name="MaxHeight">The tallest picture it writes; a bigger sequence is scaled down to fit. 0 for the sequence's own size.</param>
/// <param name="Quality">Constant quality.</param>
/// <param name="Speed">fast, medium or slow.</param>
/// <param name="Lossless">Encode without loss.</param>
/// <param name="AudioEncoder">aac or flac.</param>
/// <param name="AudioBitrate">Bits per second for AAC.</param>
public sealed record ExportPresetInfo(
    string Name,
    string Description,
    string Container,
    string Extension,
    string Codec,
    EquatableArray<string> Encoders,
    int MaxHeight,
    int Quality,
    string Speed,
    bool Lossless,
    string AudioEncoder,
    long AudioBitrate) : IEquatable<ExportPresetInfo>;

/// <summary>
/// The presets that ship with Jazz Hands.
/// </summary>
/// <remarks>
/// Phase 12's first three and a 4K one: enough to deliver, to proof and to archive. Each lists
/// NVENC first and the software encoder after it, so a machine without an NVIDIA card still
/// exports, only slower, and says so. Phase 22 adds the rest and user presets.
/// </remarks>
public static class ExportPresets
{
    /// <summary>The preset used when none is named.</summary>
    public const string Default = "youtube-1080p";

    /// <summary>Every built-in preset.</summary>
    public static ImmutableArray<ExportPresetInfo> All { get; } =
    [
        new(
            "youtube-1080p",
            "H.264 up to 1080p at the sequence's rate, AAC 320 kb/s, MP4. For uploading.",
            "mp4",
            ".mp4",
            "h264",
            ["h264_nvenc", "libx264"],
            1080,
            19,
            "medium",
            false,
            "aac",
            320_000),
        new(
            "youtube-4k",
            "HEVC up to 2160p at the sequence's rate, AAC 320 kb/s, MP4. For uploading 4K.",
            "mp4",
            ".mp4",
            "hevc",
            ["hevc_nvenc", "libx265"],
            2160,
            21,
            "medium",
            false,
            "aac",
            320_000),
        new(
            "proof",
            "H.264 at 480p, fast and small, AAC 128 kb/s, MP4. For checking a cut, not for keeping.",
            "mp4",
            ".mp4",
            "h264",
            ["h264_nvenc", "libx264"],
            480,
            28,
            "fast",
            false,
            "aac",
            128_000),
        new(
            "lossless",
            "Lossless H.264 at the sequence's size with FLAC sound, Matroska. For archiving and intermediates.",
            "matroska",
            ".mkv",
            "h264",
            ["h264_nvenc", "libx264"],
            0,
            0,
            "medium",
            true,
            "flac",
            0),
    ];

    /// <summary>A preset by name, or null.</summary>
    public static ExportPresetInfo? Find(string name) =>
        All.FirstOrDefault(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The size a preset writes for a sequence: scaled to fit its height, never up, on even numbers.</summary>
    public static (int Width, int Height) SizeFor(ExportPresetInfo preset, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(preset);

        if (preset.MaxHeight <= 0 || height <= preset.MaxHeight)
        {
            return (Even(width), Even(height));
        }

        double scale = (double)preset.MaxHeight / height;
        return (Even((int)Math.Round(width * scale)), Even(preset.MaxHeight));
    }

    private static int Even(int value) => Math.Max(2, value & ~1);
}
