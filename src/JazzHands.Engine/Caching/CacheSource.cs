using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;

namespace JazzHands.Engine.Caching;

/// <summary>
/// One stream of one media file, as the thumbnail and waveform services need it: where it is on
/// this machine, what it is called in the cache, and how long it runs.
/// </summary>
/// <param name="Hash">The content hash the cache is keyed by.</param>
/// <param name="Path">The file, or a printf pattern for an image sequence.</param>
/// <param name="StreamIndex">The stream.</param>
/// <param name="Duration">How long it runs; zero for a still.</param>
/// <param name="DemuxOptions">What an image sequence needs to open, or null.</param>
public sealed record CacheSource(
    string Hash,
    string Path,
    int StreamIndex,
    Flicks Duration,
    IReadOnlyDictionary<string, string>? DemuxOptions = null)
{
    /// <summary>A media item's picture, or null when it has none or is not hashed.</summary>
    /// <param name="item">The media.</param>
    /// <param name="projectPath">The project's file, or empty for an unsaved one.</param>
    /// <param name="streamIndex">The video stream, or -1 for the first.</param>
    public static CacheSource? Video(MediaItem item, string projectPath, int streamIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Hash.Length == 0 || (item.Kind == MediaKind.Movie && item.Info is { } info && !info.VideoStreams.Any()))
        {
            return null;
        }

        int stream = streamIndex >= 0 ? streamIndex : item.Info?.VideoStreams.FirstOrDefault()?.Index ?? 0;
        Flicks duration = item.Kind == MediaKind.Still ? Flicks.Zero : item.Duration;

        return new CacheSource(item.Hash, Resolve(item, projectPath), stream, duration, DecoderPool.SequenceOptions(item));
    }

    /// <summary>One of a media item's sound streams, or null when it is not hashed.</summary>
    public static CacheSource? Audio(MediaItem item, string projectPath, int streamIndex)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Hash.Length == 0 || streamIndex < 0)
        {
            return null;
        }

        return new CacheSource(item.Hash, Resolve(item, projectPath), streamIndex, item.Duration);
    }

    /// <summary>
    /// Where a media item's file is on this machine. An unsaved project's media paths are absolute
    /// already, and there is no project path to resolve against.
    /// </summary>
    public static string Resolve(MediaItem item, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(item);

        return string.IsNullOrEmpty(projectPath)
            ? System.IO.Path.GetFullPath(item.RelativePath)
            : ProjectPaths.Resolve(projectPath, item.RelativePath);
    }
}
