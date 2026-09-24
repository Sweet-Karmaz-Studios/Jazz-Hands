using JazzHands.Core.Export;

namespace JazzHands.Core.Commands;

/// <summary>Makes proxy files: small intra-only copies that play and scrub where the source would stall.</summary>
/// <remarks>
/// A proxy is kept in the cache under the source's content hash, so it follows the file rather
/// than the project, and another project using the same footage finds it. In the editor the work
/// goes on the export queue; a headless session makes them before it returns. Playback uses them
/// when proxies are switched on; export never does.
/// </remarks>
/// <param name="MediaId">One media item.</param>
/// <param name="All">Every movie in the project.</param>
/// <param name="Auto">Every movie heavy enough to want one: 4K and over, AV1, 10-bit HEVC.</param>
/// <param name="Scale">The proxy's width and height against the source's.</param>
/// <param name="Preset">Which proxy recipe.</param>
[Command("proxy.generate",
    Description = "Make proxy files for smooth editing of heavy footage",
    Undoable = false,
    NotUndoableReason = "A proxy is a file in the cache. It does not change the project.")]
public sealed record GenerateProxyCommand(
    [property: Option("media", "The media id")] string? MediaId = null,
    [property: Option("all", "Every movie in the project")] bool All = false,
    [property: Option("auto", "Every movie that is 4K, AV1 or 10-bit HEVC")] bool Auto = false,
    [property: Option("scale", "Size against the source: 0.5 or 0.25")] double Scale = ProxyPresets.DefaultScale,
    [property: Option("preset", "proxy-h264-intra or proxy-dnxhr-lb")] string Preset = ProxyPresets.Default) : ICommand;
