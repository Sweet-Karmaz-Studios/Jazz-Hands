using System.Collections.Concurrent;
using System.Diagnostics;
using JazzHands.Core.Model;
using JazzHands.Media.Decode;
using JazzHands.Media.Import;
using Serilog;

namespace JazzHands.Engine.Export;

/// <summary>
/// Keyframe indexes for export: from memory, then the cache database, then a scan of the file.
/// </summary>
/// <remarks>
/// A copy cuts on keyframes, so planning one needs the index of every file it copies from. A scan
/// reads every packet header in the file, about a second a gigabyte, and is kept by content hash
/// in the same cache table reverse play uses, so a file is scanned once per machine.
/// Thread safe: planning can happen on a control thread while the queue works on another.
/// </remarks>
public sealed class KeyframeLookup(CacheManager? cache = null)
{
    private readonly ILogger _log = Log.ForContext<KeyframeLookup>();
    private readonly ConcurrentDictionary<(string Key, int Stream), KeyframeIndex> _known = new();

    /// <summary>The index of one stream of a file.</summary>
    /// <param name="media">The media item, whose hash keys the cache.</param>
    /// <param name="path">Where the file is now.</param>
    /// <param name="stream">The video stream.</param>
    /// <param name="cancellationToken">Stops a scan.</param>
    public KeyframeIndex Get(MediaItem media, string path, int stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);

        string key = media.Hash.Length > 0 ? media.Hash : path;
        if (_known.TryGetValue((key, stream), out KeyframeIndex? known))
        {
            return known;
        }

        KeyframeIndex? index = cache is not null && media.Hash.Length > 0
            ? KeyframeIndex.Load(cache, media.Hash, stream)
            : null;

        if (index is null)
        {
            long started = Stopwatch.GetTimestamp();
            index = KeyframeIndex.Build(path, stream, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _log.Information(
                "Indexed {Count} keyframes of {Media} for export in {Ms:F0} ms",
                index.Count,
                media.Name,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            if (cache is not null && media.Hash.Length > 0)
            {
                index.Save(cache, media.Hash, stream);
            }
        }

        _known[(key, stream)] = index;
        return index;
    }
}
