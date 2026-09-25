using System.Collections.Concurrent;
using System.Text.Json;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Stabilization;
using JazzHands.Media.Filters;
using JazzHands.Media.Import;
using Serilog;

namespace JazzHands.Engine.Effects;

/// <summary>
/// Where stabilization's motion analyses are kept: one small JSON file per video stream, named for
/// the file's content hash, in the project's sidecar folder (<c>trailer.jazz.d/motion/</c>).
/// </summary>
/// <remarks>
/// An analysis can always be made again, so it lives beside the caches rather than in the project
/// file, and a project not saved yet keeps its analyses in the per-user cache folder. Anything that
/// draws the project (the preview, a still, an export) finds them from the project's path alone, so
/// a stabilized clip looks the same everywhere without the renderers sharing a cache.
/// </remarks>
public sealed class MotionStore
{
    private static readonly ConcurrentDictionary<string, (DateTime Written, CameraMotion Motion)> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ILogger _log = Log.ForContext<MotionStore>();

    /// <summary>A store over a folder.</summary>
    public MotionStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = folder;
    }

    /// <summary>The folder the analyses are in.</summary>
    public string Folder { get; }

    /// <summary>The store for a project: its sidecar folder, or the per-user cache before it is saved.</summary>
    public static MotionStore For(string projectPath) => new(
        string.IsNullOrEmpty(projectPath)
            ? Path.Combine(CacheManager.DefaultFolder, "motion")
            : Path.Combine(ProjectPaths.SidecarFolder(projectPath), "motion"));

    /// <summary>The file an analysis of a stream is kept in.</summary>
    public string PathFor(string hash, int streamIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        int colon = hash.IndexOf(':', StringComparison.Ordinal);
        string digits = colon >= 0 ? hash[(colon + 1)..] : hash;
        return Path.Combine(Folder, FormattableString.Invariant($"{digits}_{streamIndex}.json"));
    }

    /// <summary>The analysis of a stream, or null when there is none or it is from an older build.</summary>
    public CameraMotion? Load(string hash, int streamIndex)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        string path = PathFor(hash, streamIndex);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            DateTime written = File.GetLastWriteTimeUtc(path);
            if (Loaded.TryGetValue(path, out var known) && known.Written == written)
            {
                return known.Motion;
            }

            CameraMotion? motion = JsonSerializer.Deserialize<CameraMotion>(File.ReadAllText(path), Json);
            if (motion is null || motion.Version != CameraMotion.CurrentVersion)
            {
                return null;
            }

            Loaded[path] = (written, motion);
            return motion;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warning(exception, "Could not read the motion analysis {Path}", path);
            return null;
        }
    }

    /// <summary>Keeps an analysis, replacing any earlier one.</summary>
    public void Save(string hash, int streamIndex, CameraMotion motion)
    {
        ArgumentNullException.ThrowIfNull(motion);
        string path = PathFor(hash, streamIndex);
        Directory.CreateDirectory(Folder);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(motion, Json));
        File.Move(temporary, path, overwrite: true);
        Loaded[path] = (File.GetLastWriteTimeUtc(path), motion);
    }

    /// <summary>Analyses a media item's video stream and keeps the result.</summary>
    /// <param name="item">The media item.</param>
    /// <param name="path">Where its file is.</param>
    /// <param name="streamIndex">Which video stream.</param>
    /// <param name="progress">Told the fraction done.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    public CameraMotion Analyze(MediaItem item, string path, int streamIndex, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        MediaStream? stream = item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == streamIndex);
        long frames = stream?.FrameRate is { Num: > 0 } rate ? item.Duration.ToFrames(rate, Core.Time.RoundingMode.Nearest) : 0;
        CameraMotion motion = MotionAnalyzer.Analyze(path, streamIndex, frames, progress: progress, cancellationToken: cancellationToken);
        Save(item.Hash, streamIndex, motion);
        return motion;
    }
}
