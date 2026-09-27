using System.Collections.Concurrent;
using JazzHands.Core.Model;
using JazzHands.Media.Analysis;
using JazzHands.Media.Import;
using Serilog;

namespace JazzHands.Engine.Caching;

/// <summary>
/// Scene-cut measurements for media files: measured once per file content and video stream, then
/// kept in the cache database and in memory, so any threshold after the first is instant.
/// </summary>
/// <remarks>
/// Measuring holds the calling thread for about a third of the file's running time (1080p60
/// H.264 on the reference machine), so the editor calls <see cref="Measure"/> from a background
/// task with progress and a way to stop it (the scene-cut dialog), and the commands that follow
/// find it here already. Two callers asking for the same file at once both measure it; the second
/// result replaces the first, which is the same.
/// </remarks>
public sealed class SceneCutService(CacheManager? cache = null)
{
    private readonly ConcurrentDictionary<(string Hash, int Stream), SceneMeasurements> _known = new();
    private readonly ILogger _log = Log.ForContext<SceneCutService>();

    /// <summary>The measurements already made for a file's stream, or null.</summary>
    public SceneMeasurements? Cached(string hash, int streamIndex)
    {
        if (hash.Length == 0)
        {
            return null;
        }

        if (_known.TryGetValue((hash, streamIndex), out SceneMeasurements? known))
        {
            return known;
        }

        if (cache?.GetAnalysis(hash, streamIndex, SceneMeasurements.CacheKind) is { } bytes
            && SceneMeasurements.FromBytes(bytes) is { } stored)
        {
            _known[(hash, streamIndex)] = stored;
            return stored;
        }

        return null;
    }

    /// <summary>The measurements for a media item's stream: the cached ones, or new ones read from its file.</summary>
    /// <param name="item">The media item.</param>
    /// <param name="path">Where its file is.</param>
    /// <param name="stream">The video stream.</param>
    /// <param name="progress">Told the fraction done, when the file has to be read.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public SceneMeasurements Measure(MediaItem item, string path, MediaStream stream, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(stream);

        if (Cached(item.Hash, stream.Index) is { } known)
        {
            progress?.Report(1.0);
            return known;
        }

        long expected = stream.FrameRate is { } rate && !rate.IsZero
            ? (long)(stream.Duration.ToSeconds() * rate.ToDouble())
            : 0;
        SceneMeasurements measured = SceneDetector.Measure(path, stream.Index, expected, progress, cancellationToken);

        if (item.Hash.Length > 0)
        {
            _known[(item.Hash, stream.Index)] = measured;
            try
            {
                cache?.PutAnalysis(item.Hash, stream.Index, SceneMeasurements.CacheKind, measured.ToBytes());
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
            {
                // Losing the cache costs the next caller a read of the file, nothing more.
                _log.Warning(exception, "Could not keep the scene cuts of {Media} in the cache", item.Name);
            }
        }

        return measured;
    }

    /// <summary>Forgets what is held in memory about one file content.</summary>
    public void Forget(string hash)
    {
        foreach ((string Hash, int Stream) key in _known.Keys.Where(key => string.Equals(key.Hash, hash, StringComparison.Ordinal)))
        {
            _known.TryRemove(key, out _);
        }
    }

    /// <summary>Forgets what is held in memory, after the cache is cleared.</summary>
    public void Clear() => _known.Clear();
}
