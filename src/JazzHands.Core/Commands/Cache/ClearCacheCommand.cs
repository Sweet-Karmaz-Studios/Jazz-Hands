namespace JazzHands.Core.Commands;

/// <summary>Empties the cache, or parts of it.</summary>
/// <remarks>
/// With no part named, clears everything that is made again on demand: probes, keyframe indexes,
/// thumbnails and waveforms. Proxies take minutes to make again, so they go only when named or
/// with <c>--all</c>.
/// </remarks>
/// <param name="MediaId">Only what is cached about one media item.</param>
/// <param name="Thumbnails">Thumbnails.</param>
/// <param name="Waveforms">Waveforms.</param>
/// <param name="Probes">Probes.</param>
/// <param name="Keyframes">Keyframe indexes.</param>
/// <param name="Proxies">Proxy files.</param>
/// <param name="All">Everything, proxies included.</param>
[Command("cache.clear",
    Description = "Empty the cache, or parts of it",
    Undoable = false,
    NotUndoableReason = "The cache is not part of the project, and what it held is made again when it is needed.")]
public sealed record ClearCacheCommand(
    [property: Option("media", "Only what is cached about this media id")] string? MediaId = null,
    [property: Option("thumbs", "Thumbnails")] bool Thumbnails = false,
    [property: Option("waveforms", "Waveforms")] bool Waveforms = false,
    [property: Option("probes", "Probes")] bool Probes = false,
    [property: Option("keyframes", "Keyframe indexes")] bool Keyframes = false,
    [property: Option("proxies", "Proxy files")] bool Proxies = false,
    [property: Option("all", "Everything, proxies included")] bool All = false) : ICommand;
