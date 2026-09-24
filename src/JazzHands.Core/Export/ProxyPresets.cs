using System.Collections.Immutable;
using JazzHands.Core.Model;

namespace JazzHands.Core.Export;

/// <summary>A recipe for proxy files: small, every frame a keyframe, fast to decode anywhere.</summary>
/// <param name="Name">What it is called on the command line: proxy-h264-intra.</param>
/// <param name="Description">One line for help.</param>
/// <param name="Container">The FFmpeg muxer.</param>
/// <param name="Extension">The file extension.</param>
/// <param name="Codec">h264 or dnxhr.</param>
/// <param name="Encoders">FFmpeg encoders in the order to try them.</param>
/// <param name="Quality">Constant quality, where the codec has one.</param>
public sealed record ProxyPresetInfo(
    string Name,
    string Description,
    string Container,
    string Extension,
    string Codec,
    EquatableArray<string> Encoders,
    int Quality) : IEquatable<ProxyPresetInfo>;

/// <summary>
/// The proxy recipes, and the rule for which media is worth a proxy.
/// </summary>
/// <remarks>
/// Both are intra only. A proxy exists to be scrubbed, and a scrub to a frame in the middle of a
/// long group of pictures decodes the whole group; with every frame a keyframe it decodes one.
/// H.264 is the default because every decoder on every machine has it; DNxHR LB is the editor's
/// intermediate, bigger and cheaper still to decode on the CPU.
///
/// The encoders are software. A proxy is made in the background while someone may be playing
/// footage or a game on the GPU, and x264 at veryfast on a spare core is quick enough at half
/// size.
/// </remarks>
public static class ProxyPresets
{
    /// <summary>The preset used when none is named.</summary>
    public const string Default = "proxy-h264-intra";

    /// <summary>The scale used when none is given: half the width and height.</summary>
    public const double DefaultScale = 0.5;

    /// <summary>Every proxy preset.</summary>
    public static ImmutableArray<ProxyPresetInfo> All { get; } =
    [
        new(
            "proxy-h264-intra",
            "H.264, every frame a keyframe, QuickTime. Plays everywhere; the default.",
            "mov",
            ".mov",
            "h264",
            ["libx264"],
            23),
        new(
            "proxy-dnxhr-lb",
            "DNxHR LB, 4:2:2 intra, QuickTime. Larger, and the cheapest of all to decode.",
            "mov",
            ".mov",
            "dnxhr",
            ["dnxhd"],
            0),
    ];

    /// <summary>A preset by name, or null.</summary>
    public static ProxyPresetInfo? Find(string name) =>
        All.FirstOrDefault(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a file is heavy enough to edit that a proxy is worth suggesting: 4K and over,
    /// AV1, or 10-bit HEVC. Those are what make a scrub stall on a machine without the right
    /// hardware decoder, or with it busy.
    /// </summary>
    public static bool IsWorthAProxy(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Info?.VideoStreams.FirstOrDefault() is not { } video)
        {
            return false;
        }

        bool big = Math.Max(video.Width, video.Height) >= 3840 || Math.Min(video.Width, video.Height) >= 2160;
        bool av1 = string.Equals(video.Codec, "av1", StringComparison.OrdinalIgnoreCase);
        bool hevc10 = string.Equals(video.Codec, "hevc", StringComparison.OrdinalIgnoreCase) && video.BitDepth >= 10;

        return big || av1 || hevc10;
    }

    /// <summary>The size a proxy is written at: the source scaled, on even numbers, at least 16 high.</summary>
    public static (int Width, int Height) SizeFor(int width, int height, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0 || scale > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "A proxy is between none and all of the source's size.");
        }

        int w = Math.Max(16, (int)Math.Round(width * scale / 2.0, MidpointRounding.AwayFromZero) * 2);
        int h = Math.Max(16, (int)Math.Round(height * scale / 2.0, MidpointRounding.AwayFromZero) * 2);
        return (w, h);
    }
}
