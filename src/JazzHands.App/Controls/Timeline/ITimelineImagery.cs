using System.Windows.Media;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Core.Time;

namespace JazzHands.App.Controls.Timeline;

/// <summary>One thumbnail of a clip's picture.</summary>
/// <param name="SourceTime">The source time it stands for, on its grid.</param>
/// <param name="TimelineTime">Where its left edge goes on the timeline, with the clip's speed and direction applied.</param>
/// <param name="Image">The picture, frozen.</param>
public sealed record ThumbnailTile(Flicks SourceTime, Flicks TimelineTime, ImageSource Image);

/// <summary>
/// A clip's sound as peaks: for each pixel column, the lowest and highest sample, -1 to 1.
/// </summary>
/// <param name="Minimum">The lowest sample in each column.</param>
/// <param name="Maximum">The highest sample in each column.</param>
public sealed record WaveformPeaks(float[] Minimum, float[] Maximum);

/// <summary>
/// Where the timeline gets pictures and waveforms for clips.
/// </summary>
/// <remarks>
/// Asked while drawing, so it must answer at once: what it has cached, or nothing, in which case
/// the timeline draws a placeholder and asks again when <see cref="Changed"/> says there is more.
/// Filling the cache is the implementer's business, on its own threads and at visible-first
/// priority; <see cref="Services.TimelineImagery"/> is the one the editor uses, over the engine's
/// thumbnail and waveform services. <see cref="NoImagery"/> has nothing, and every clip draws its
/// placeholder.
/// </remarks>
public interface ITimelineImagery
{
    /// <summary>Raised on the UI thread when something new is cached, so the clips are drawn again.</summary>
    event EventHandler? Changed;

    /// <summary>Thumbnails for the part of a clip between two timeline times, at a zoom.</summary>
    /// <param name="clip">The clip.</param>
    /// <param name="from">The first timeline time on screen.</param>
    /// <param name="to">The last.</param>
    /// <param name="pixelsPerSecond">The zoom.</param>
    /// <param name="tileWidth">How wide a thumbnail is drawn, so neighbours can be spaced not to overlap.</param>
    /// <param name="tiles">The thumbnails ready, in no particular order.</param>
    /// <returns>False when none are ready yet.</returns>
    bool TryGetThumbnails(ClipView clip, Flicks from, Flicks to, double pixelsPerSecond, double tileWidth, out IReadOnlyList<ThumbnailTile> tiles);

    /// <summary>A clip's waveform between two timeline times, one column per pixel.</summary>
    /// <returns>False when it is not ready yet.</returns>
    bool TryGetWaveform(ClipView clip, Flicks from, Flicks to, int columns, out WaveformPeaks peaks);
}

/// <summary>Imagery with nothing in it: every clip draws its placeholder.</summary>
public sealed class NoImagery : ITimelineImagery
{
    /// <summary>The one instance.</summary>
    public static NoImagery Instance { get; } = new();

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public bool TryGetThumbnails(ClipView clip, Flicks from, Flicks to, double pixelsPerSecond, double tileWidth, out IReadOnlyList<ThumbnailTile> tiles)
    {
        tiles = [];
        return false;
    }

    /// <inheritdoc />
    public bool TryGetWaveform(ClipView clip, Flicks from, Flicks to, int columns, out WaveformPeaks peaks)
    {
        peaks = new WaveformPeaks([], []);
        return false;
    }
}
