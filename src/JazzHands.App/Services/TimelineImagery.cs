using JazzHands.App.Controls.Timeline;
using JazzHands.App.ViewModels.Timeline;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Caching;
using JazzHands.Media.Waveforms;

namespace JazzHands.App.Services;

/// <summary>
/// The timeline's thumbnails and waveforms, from the engine's caches.
/// </summary>
/// <remarks>
/// <para>
/// A clip asks for the stretch of its source that is on screen, on the grid the zoom calls for
/// (<see cref="ThumbnailService.SpacingFor"/>, in source time, so a clip at twice speed takes
/// thumbnails twice as far apart). What is ready is drawn, and the rest is queued visible first.
/// The first time a clip is drawn at a spacing, the whole of it is queued behind at idle priority,
/// so scrolling along it finds the pictures already there.
/// </para>
/// <para>
/// Each tile carries its timeline position, worked out here from the clip's speed and direction:
/// a reversed clip's pictures run right to left, and a freeze frame is one picture repeated.
/// </para>
/// </remarks>
public sealed class TimelineImagery : ITimelineImagery
{
    private readonly ISession _session;
    private readonly CachedThumbnails _media;
    private readonly HashSet<(string Clip, string Hash, long Spacing)> _prefetched = [];

    private bool _decibels;
    private EventHandler? _scaleChanged;

    /// <summary>Imagery over the editor's caches.</summary>
    /// <param name="session">The session whose clips are drawn.</param>
    /// <param name="media">The thumbnail and waveform caches.</param>
    /// <param name="ui">Where changes are taken up.</param>
    /// <param name="editor">The editor's settings, for the waveform scale; null draws them linear.</param>
    public TimelineImagery(ISession session, CachedThumbnails media, IUiDispatcher? ui = null, Engine.Settings.SettingsSection<EditorSettings>? editor = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(media);

        _session = session;
        _media = media;
        if (editor is not null)
        {
            _decibels = editor.Current.WaveformsInDecibels;
            editor.Saved += (_, saved) => (ui ?? new InlineDispatcher()).Post(() => Decibels = saved.WaveformsInDecibels);
        }

        // A clip named in a change may have been trimmed out over source that was never
        // prefetched: its whole range is asked for again the next time it is drawn.
        _session.ProjectChanged += (_, e) =>
        {
            if (e.ChangedIds.IsDefaultOrEmpty)
            {
                return;
            }

            HashSet<string> changed = [.. e.ChangedIds];
            (ui ?? new InlineDispatcher()).Post(() => _prefetched.RemoveWhere(key => changed.Contains(key.Clip)));
        };
    }

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add
        {
            _media.Changed += value;
            _scaleChanged += value;
        }

        remove
        {
            _media.Changed -= value;
            _scaleChanged -= value;
        }
    }

    /// <summary>
    /// Waveforms on a decibel scale (<see cref="WaveformScale.Decibels"/>) rather than linear; the
    /// timeline redraws when it changes.
    /// </summary>
    public bool Decibels
    {
        get => _decibels;
        set
        {
            if (_decibels != value)
            {
                _decibels = value;
                _scaleChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <inheritdoc />
    public bool TryGetThumbnails(ClipView view, Flicks from, Flicks to, double pixelsPerSecond, double tileWidth, out IReadOnlyList<ThumbnailTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(view);
        tiles = [];

        Clip clip = view.Clip;
        if (clip.MediaId is not { } id || _session.Project.MediaItem(id) is not { } item || pixelsPerSecond <= 0)
        {
            return false;
        }

        if (clip.IsHold)
        {
            // One picture, repeated along the clip. A grid of one flick has exactly one point in
            // a stretch one flick long, which is the held frame itself, taken exactly.
            IReadOnlyList<(ThumbnailImage Image, System.Windows.Media.ImageSource Picture)> held =
                _media.Strip(item, clip.SourceStreamIndex, new Flicks(1), clip.SourceIn, clip.SourceIn + new Flicks(1));
            if (held.Count == 0)
            {
                return false;
            }

            Flicks step = Flicks.Max(Flicks.FromSeconds(tileWidth / pixelsPerSecond), Flicks.FromFrames(1, Rational.Fps60));
            var repeated = new List<ThumbnailTile>();
            for (Flicks at = clip.Start + ((Flicks.Max(from, clip.Start) - clip.Start) / step.Value * step.Value); at < to; at += step)
            {
                repeated.Add(new ThumbnailTile(clip.SourceIn, at, held[0].Picture));
            }

            tiles = repeated;
            return repeated.Count > 0;
        }

        double speed = Math.Max(Math.Abs(clip.EffectiveSpeed.ToDouble()), 1e-6);
        Flicks spacing = ThumbnailService.SpacingFor(pixelsPerSecond / speed, tileWidth);

        Flicks a = clip.SourceTimeAt(from);
        Flicks b = clip.SourceTimeAt(to);
        Flicks low = Flicks.Max(Flicks.Min(a, b) - spacing, clip.SourceIn);
        Flicks high = Flicks.Min(Flicks.Max(a, b) + spacing, clip.SourceOut);

        if (_prefetched.Add((clip.Id, item.Hash, spacing.Value)))
        {
            if (_prefetched.Count > 20_000)
            {
                _prefetched.Clear();
            }

            _media.Prefetch(item, clip.SourceStreamIndex, spacing, clip.SourceIn, clip.SourceOut);
        }

        var ready = new List<ThumbnailTile>();
        foreach ((ThumbnailImage image, System.Windows.Media.ImageSource picture) in _media.Strip(item, clip.SourceStreamIndex, spacing, low, high))
        {
            // A reversed clip shows a tile's stretch of source right to left, so its left edge on
            // the timeline is where the end of that stretch plays.
            Flicks edge = clip.Reverse ? Flicks.Min(image.Time + spacing, clip.SourceOut) : Flicks.Max(image.Time, clip.SourceIn);
            ready.Add(new ThumbnailTile(image.Time, TimelineTimeOf(clip, edge), picture));
        }

        tiles = ready;
        return ready.Count > 0;
    }

    /// <inheritdoc />
    public bool TryGetWaveform(ClipView view, Flicks from, Flicks to, int columns, out WaveformPeaks peaks)
    {
        ArgumentNullException.ThrowIfNull(view);
        peaks = new WaveformPeaks([], []);

        Clip clip = view.Clip;
        if (columns < 2 || to <= from || clip.IsHold
            || clip.MediaId is not { } id || _session.Project.MediaItem(id) is not { } item
            || _media.Peaks(item, clip.SourceStreamIndex) is not { Windows: > 0 } source)
        {
            return false;
        }

        float[] minimum = new float[columns];
        float[] maximum = new float[columns];
        Flicks column = (to - from) / columns;

        for (int index = 0; index < columns; index++)
        {
            Flicks start = from + (column * index);
            long first = AudioPeaks.WindowAt(clip.SourceTimeAt(start));
            long last = AudioPeaks.WindowAt(clip.SourceTimeAt(start + column));

            if (source.Range(Math.Min(first, last), Math.Max(first, last) + 1, out float low, out float high))
            {
                minimum[index] = _decibels ? WaveformScale.Decibels(low) : low;
                maximum[index] = _decibels ? WaveformScale.Decibels(high) : high;
            }
        }

        peaks = new WaveformPeaks(minimum, maximum);
        return true;
    }

    /// <summary>Where a source time plays on the timeline, the inverse of <see cref="Clip.SourceTimeAt"/>.</summary>
    internal static Flicks TimelineTimeOf(Clip clip, Flicks sourceTime)
    {
        Flicks offset = clip.Reverse ? clip.SourceOut - sourceTime : sourceTime - clip.SourceIn;
        Rational speed = clip.EffectiveSpeed;
        return clip.Start + new Flicks((long)((Int128)offset.Value * speed.Den / speed.Num));
    }
}
