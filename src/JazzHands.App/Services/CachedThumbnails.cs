using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Caching;
using JazzHands.Media.Waveforms;

namespace JazzHands.App.Services;

/// <summary>
/// Pictures and waveforms of media for the views: the engine's caches, turned into things WPF
/// draws, with one event on the UI thread when more is ready.
/// </summary>
public interface IMediaImagery
{
    /// <summary>Raised on the UI thread, at most every 50 ms, when more pictures or peaks are ready.</summary>
    event EventHandler? Changed;

    /// <summary>A media item's poster: a picture a tenth of the way in, or null until one is ready.</summary>
    ImageSource? Poster(MediaItem item);

    /// <summary>
    /// A media item's picture a fraction of the way through, for scrubbing across a tile. On a
    /// grid of forty steps, so a mouse moving across a tile asks for forty pictures, not four hundred.
    /// </summary>
    ImageSource? At(MediaItem item, double fraction);

    /// <summary>A strip of pictures of a stretch of a source, as far as it is ready.</summary>
    IReadOnlyList<(ThumbnailImage Image, ImageSource Picture)> Strip(MediaItem item, int streamIndex, Flicks spacing, Flicks from, Flicks to);

    /// <summary>A source's peaks, as far as they are read, or null.</summary>
    AudioPeaks? Peaks(MediaItem item, int streamIndex);
}

/// <summary>
/// The engine's thumbnail and waveform services, for WPF.
/// </summary>
/// <remarks>
/// JPEGs are decoded to frozen bitmaps on the UI thread as they are first drawn, a few hundred
/// microseconds each at 160 pixels, and kept in a least recently used map as big as the engine's.
/// The engine's ready events arrive on its workers, dozens a second while a strip fills; they are
/// folded into one <see cref="Changed"/> per 50 ms so the timeline redraws at a steady rate while
/// thumbnails pour in.
/// </remarks>
public sealed class CachedThumbnails : IMediaImagery, IDisposable
{
    private const int ScrubSteps = 40;

    private readonly ISession _session;
    private readonly ThumbnailService _thumbnails;
    private readonly WaveformService _waveforms;
    private readonly IUiDispatcher _ui;
    private readonly MemoryLru<(string Hash, int Stream, long Time, long Frame), ImageSource> _pictures =
        new(ThumbnailService.MemoryCapacity);

    private readonly Dictionary<(string Id, string Hash, int Stream, bool Audio), CacheSource?> _sources = [];
    private int _pending;

    /// <summary>Wraps the engine's services.</summary>
    public CachedThumbnails(ISession session, ThumbnailService thumbnails, WaveformService waveforms, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(waveforms);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _thumbnails = thumbnails;
        _waveforms = waveforms;
        _ui = ui;

        _thumbnails.Ready += OnReady;
        _waveforms.Ready += OnReady;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public ImageSource? Poster(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (Video(item, -1) is not { } source)
        {
            return null;
        }

        // A tenth of the way in, no more than five seconds: past the black a recording starts with.
        Flicks time = Flicks.Min(source.Duration / 10, Flicks.FromSeconds(5));
        Flicks tolerance = Flicks.Min(source.Duration / 20, Flicks.FromSeconds(2));

        return _thumbnails.At(source, time, tolerance, WorkPriority.Near) is { } image ? Picture(source, image) : null;
    }

    /// <inheritdoc />
    public ImageSource? At(MediaItem item, double fraction)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (Video(item, -1) is not { } source)
        {
            return null;
        }

        if (source.Duration <= Flicks.Zero)
        {
            return Poster(item);
        }

        Flicks step = source.Duration / ScrubSteps;
        long index = Math.Clamp((long)(Math.Clamp(fraction, 0.0, 1.0) * ScrubSteps), 0, ScrubSteps - 1);

        return _thumbnails.At(source, step * index, step / 2, WorkPriority.Visible) is { } image ? Picture(source, image) : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<(ThumbnailImage Image, ImageSource Picture)> Strip(MediaItem item, int streamIndex, Flicks spacing, Flicks from, Flicks to)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (Video(item, streamIndex) is not { } source)
        {
            return [];
        }

        IReadOnlyList<ThumbnailImage> images = _thumbnails.Strip(source, spacing, from, to, WorkPriority.Visible);
        var pictures = new List<(ThumbnailImage, ImageSource)>(images.Count);

        foreach (ThumbnailImage image in images)
        {
            pictures.Add((image, Picture(source, image)));
        }

        return pictures;
    }

    /// <summary>Queues the whole of a stretch at idle priority, so a clip fills in beyond what is on screen.</summary>
    public void Prefetch(MediaItem item, int streamIndex, Flicks spacing, Flicks from, Flicks to)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (Video(item, streamIndex) is { } source)
        {
            _thumbnails.Strip(source, spacing, from, to, WorkPriority.Idle);
        }
    }

    /// <inheritdoc />
    public AudioPeaks? Peaks(MediaItem item, int streamIndex)
    {
        ArgumentNullException.ThrowIfNull(item);

        CacheSource? source = Source(item, streamIndex, audio: true);
        return source is null ? null : _waveforms.Get(source, WorkPriority.Visible);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _thumbnails.Ready -= OnReady;
        _waveforms.Ready -= OnReady;
    }

    private CacheSource? Video(MediaItem item, int streamIndex) => Source(item, streamIndex, audio: false);

    /// <summary>A source per media item, stream and content, made once rather than on every draw.</summary>
    private CacheSource? Source(MediaItem item, int streamIndex, bool audio)
    {
        var key = (item.Id, item.Hash, streamIndex, audio);
        if (!_sources.TryGetValue(key, out CacheSource? source))
        {
            if (_sources.Count > 4096)
            {
                _sources.Clear();
            }

            source = audio
                ? CacheSource.Audio(item, _session.ProjectPath, streamIndex)
                : CacheSource.Video(item, _session.ProjectPath, streamIndex);
            _sources[key] = source;
        }

        return source;
    }

    private ImageSource Picture(CacheSource source, ThumbnailImage image)
    {
        var key = (source.Hash, source.StreamIndex, image.Time.Value, image.FrameTime.Value);
        if (_pictures.TryGet(key, out ImageSource? picture))
        {
            return picture;
        }

        using var stream = new MemoryStream(image.Jpeg, writable: false);
        BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze();

        _pictures.Set(key, frame);
        return frame;
    }

    private void OnReady(object? sender, string hash)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(50).ContinueWith(
            _ =>
            {
                Volatile.Write(ref _pending, 0);
                _ui.Post(() => Changed?.Invoke(this, EventArgs.Empty));
            },
            TaskScheduler.Default);
    }
}
