namespace JazzHands.Core.Commands;

/// <summary>Asks what the cache holds, where it is, and how big it is allowed to get.</summary>
[Query("cache.stats", Description = "Show what the cache holds and its size limit")]
public sealed record GetCacheStatsQuery : IQuery<CacheStatsInfo>;

/// <summary>What the cache holds.</summary>
/// <param name="Location">The cache folder.</param>
/// <param name="CapBytes">The most thumbnails and waveforms may take, or 0 for no limit.</param>
/// <param name="BlobBytes">What thumbnails and waveforms take now.</param>
/// <param name="DatabaseBytes">The cache database.</param>
/// <param name="Probes">Files whose probe is cached.</param>
/// <param name="KeyframeIndexes">Streams whose keyframes are indexed.</param>
/// <param name="Thumbnails">Thumbnails cached.</param>
/// <param name="Waveforms">Waveforms cached.</param>
/// <param name="Proxies">Proxy files.</param>
/// <param name="ProxyBytes">What they take. Proxies are not counted against the cap.</param>
/// <param name="Evicted">Blobs evicted to stay under the cap since the editor started.</param>
/// <param name="PendingWork">Thumbnails and waveforms waiting to be made.</param>
/// <param name="ProxiesEnabled">True when playback uses proxies.</param>
public sealed record CacheStatsInfo(
    string Location,
    long CapBytes,
    long BlobBytes,
    long DatabaseBytes,
    int Probes,
    int KeyframeIndexes,
    int Thumbnails,
    int Waveforms,
    int Proxies,
    long ProxyBytes,
    long Evicted,
    int PendingWork,
    bool ProxiesEnabled);
