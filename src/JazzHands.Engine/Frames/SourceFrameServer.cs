using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Diagnostics;
using JazzHands.Core.Time;
using JazzHands.Engine.Diagnostics;
using JazzHands.Media.Decode;
using JazzHands.Render;
using JazzHands.Render.Frames;
using Serilog;

namespace JazzHands.Engine.Frames;

/// <summary>Which way the playhead is going, which decides what is decoded ahead.</summary>
public enum PlayDirection
{
    /// <summary>Not playing. Nothing is decoded ahead.</summary>
    Still,

    /// <summary>Forwards, so frames after the request are worth having.</summary>
    Forward,

    /// <summary>Backwards, which means decoding a group of pictures forwards and keeping it.</summary>
    Reverse,
}

/// <summary>
/// Turns "this clip, at this timeline moment" into a frame in video memory.
/// </summary>
/// <remarks>
/// Everything the compositor draws starts here. The mapping is the interesting part: a clip has a
/// start on the timeline, a start in the source, a speed and possibly a direction, and the frame
/// wanted is the one displayed at the source time that comes out of all four.
///
/// It sits in the engine rather than in the render layer, because it needs the project model and
/// the decoders and the render layer is not allowed to see either. The compositor is handed
/// frames; it does not go looking for them.
///
/// Thread affine: it owns a decoder pool and a frame cache, and both are.
/// </remarks>
public sealed class SourceFrameServer : IDisposable
{
    private readonly ILogger _log = Log.ForContext<SourceFrameServer>();
    private readonly DecoderPool _decoders;
    private readonly FrameCache _cache;
    private readonly DecoderFrameCopier _copier;
    private readonly PlaneUploader _uploader;
    private readonly DiagnosticsLog? _notices;
    private readonly bool _ownsDecoders;
    private readonly Dictionary<(int Width, int Height, FFmpeg.AutoGen.AVPixelFormat From), PixelConverter> _converters = [];
    private bool _disposed;

    /// <summary>Creates a frame server.</summary>
    /// <param name="decoders">Where decoders come from.</param>
    /// <param name="cache">Where frames are kept.</param>
    /// <param name="device">The device frames are copied and uploaded on.</param>
    /// <param name="notices">Where fallbacks are reported, or null to keep them to the log.</param>
    /// <param name="ownsDecoders">False when the pool is shared with something else.</param>
    public SourceFrameServer(
        DecoderPool decoders,
        FrameCache cache,
        RenderDevice device,
        DiagnosticsLog? notices = null,
        bool ownsDecoders = true)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(device);

        _decoders = decoders;
        _cache = cache;
        _copier = new DecoderFrameCopier(device);
        _uploader = new PlaneUploader(device);
        _notices = notices;
        _ownsDecoders = ownsDecoders;

        if (notices is not null)
        {
            // A file that decodes in software still plays, so nothing throws and nobody would
            // find out. This is how it reaches the media panel's badge and jazz diagnostics list.
            _decoders.FellBack += OnFellBack;
        }
    }

    /// <summary>How many frames ahead of a request the playhead decoder runs.</summary>
    /// <remarks>
    /// Four, as the phase asks. Enough that a decode that takes longer than a frame interval does
    /// not show, and few enough that a direction change throws away almost nothing.
    /// </remarks>
    public int DecodeAhead { get; set; } = 4;

    /// <summary>
    /// What to decode in place of a media item: its proxy, or null for the item itself. Playback
    /// sets it from the proxy service; export leaves it null, which is how export never reads a
    /// proxy.
    /// </summary>
    public Func<MediaItem, MediaItem?>? Substitute { get; set; }

    /// <summary>The frame cache, for diagnostics and for pinning around the playhead.</summary>
    public FrameCache Cache => _cache;

    /// <summary>The decoder pool, for diagnostics.</summary>
    public DecoderPool Decoders => _decoders;

    /// <summary>Frames served from a decode rather than from the cache.</summary>
    public long Decoded { get; private set; }

    /// <summary>Frames copied out of a hardware decoder's surfaces.</summary>
    public long Copied => _copier.Copied;

    /// <summary>Planes uploaded from a software decoder.</summary>
    public long Uploaded => _uploader.Uploaded;

    /// <summary>
    /// The source position a clip shows at a timeline position, snapped to the source's frame
    /// grid.
    /// </summary>
    /// <remarks>
    /// <see cref="Clip.SourceTimeAt"/> does the arithmetic, including speed and reverse. What is
    /// added here is the snap: an unsnapped time between two frames would pick whichever side
    /// rounding fell on, and a clip at 1001/1000 speed would judder as the error walked across
    /// the boundary.
    /// </remarks>
    public static Flicks SourceTimeFor(Clip clip, Flicks timelineTime, Rational sourceRate)
    {
        ArgumentNullException.ThrowIfNull(clip);

        Flicks raw = clip.SourceTimeAt(timelineTime);

        if (sourceRate.IsZero || sourceRate.Num <= 0)
        {
            return raw;
        }

        // Floor, not nearest: the frame on screen at time t is the one that started at or before
        // it, which is the same rule the seeker follows.
        long frame = raw.ToFrames(sourceRate, RoundingMode.Floor);

        if (clip.Reverse)
        {
            // A source range is half open, so a reversed clip's first instant maps to SourceOut,
            // which is one past the last frame there is. Stepping back one frame puts the two
            // ends where they belong: the clip opens on the last frame of the source and closes
            // on the first.
            frame--;
        }

        return Flicks.FromFrames(Math.Max(frame, 0), sourceRate);
    }

    /// <summary>
    /// The frame a clip shows at a timeline position.
    /// </summary>
    /// <param name="project">The project, for resolving the clip's media.</param>
    /// <param name="clip">The clip.</param>
    /// <param name="timelineTime">Where on the timeline.</param>
    /// <param name="projectPath">Where the project lives, for resolving a relative media path.</param>
    /// <param name="direction">Which way the playhead is moving, which decides what is decoded ahead.</param>
    /// <param name="mode">Exact for the frame at the time, nearest for the keyframe before it.</param>
    /// <param name="lane">Which layer asks, so two layers of one file keep separate decoders.</param>
    /// <param name="readAhead">
    /// Decode a few frames past this one while the decoder is warm. Off when the caller paces its
    /// own decoding ahead, because a burst of decodes is a burst of GPU work the next present
    /// waits behind.
    /// </param>
    /// <returns>
    /// The frame, owned by the cache, or null when the clip has no media or the time is past the
    /// end of it.
    /// </returns>
    public FrameTexture? GetSourceFrame(
        Project project,
        Clip clip,
        Flicks timelineTime,
        string projectPath = "",
        PlayDirection direction = PlayDirection.Still,
        SeekMode mode = SeekMode.Exact,
        int lane = 0,
        bool readAhead = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (clip.MediaId is not { } mediaId)
        {
            return null;
        }

        MediaItem? item = project.MediaItem(mediaId);
        if (item is null || item.Hash.Length == 0)
        {
            return null;
        }

        (item, int stream, _) = Decodable(item, clip.SourceStreamIndex);

        Rational rate = RateOf(item, stream);

        // A still is one picture however long its clip, and its decoder has it only at zero.
        Flicks sourceTime = item.Kind == MediaKind.Still ? Flicks.Zero : SourceTimeFor(clip, timelineTime, rate);
        var key = new FrameKey(item.Hash, stream, sourceTime);

        if (_cache.Get(key) is { } cached)
        {
            return cached;
        }

        string path = ResolveMedia(projectPath, item);
        DecoderRole role = direction == PlayDirection.Still ? DecoderRole.Seek : DecoderRole.Playhead;

        using DecoderLease lease = _decoders.Rent(
            item,
            path,
            stream,
            role,
            project.Settings.FrameRate,
            lane);

        int bitDepth = BitDepthOf(item, stream);
        FrameTexture? served = DecodeTo(lease, item, key, sourceTime, rate, bitDepth, mode);

        if (served is not null && readAhead && direction != PlayDirection.Still)
        {
            ReadAhead(lease, item, stream, rate, direction, bitDepth);
        }

        return served;
    }

    /// <summary>The frame a clip shows at a timeline position if it is already cached, without decoding anything.</summary>
    public FrameTexture? GetCachedFrame(Project project, Clip clip, Flicks timelineTime)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (clip.MediaId is not { } mediaId || project.MediaItem(mediaId) is not { Hash.Length: > 0 } item)
        {
            return null;
        }

        (item, int stream, _) = Decodable(item, clip.SourceStreamIndex);
        Rational rate = RateOf(item, stream);
        return _cache.Get(new FrameKey(item.Hash, stream, item.Kind == MediaKind.Still ? Flicks.Zero : SourceTimeFor(clip, timelineTime, rate)));
    }

    /// <summary>
    /// Fills the cache with the group of pictures a time sits inside, which is how a source is
    /// played backwards.
    /// </summary>
    /// <remarks>
    /// There is no such thing as decoding backwards: a frame depends on the ones before it. The
    /// only way to show a group in reverse is to decode it forwards, keep all of it, and then
    /// hand it out in the other order. This is what makes the frame cache load bearing rather
    /// than an optimisation.
    /// </remarks>
    public int PrimeGop(
        Project project,
        Clip clip,
        Flicks timelineTime,
        KeyframeIndex index,
        string projectPath = "",
        int lane = 0)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(index);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (clip.MediaId is not { } mediaId ||
            project.MediaItem(mediaId) is not { } item ||
            item.Hash.Length == 0)
        {
            return 0;
        }

        (item, int stream, _) = Decodable(item, clip.SourceStreamIndex);

        Rational rate = RateOf(item, stream);
        Flicks sourceTime = SourceTimeFor(clip, timelineTime, rate);
        TimeRange gop = index.GopContaining(sourceTime);

        string path = ResolveMedia(projectPath, item);
        using DecoderLease lease = _decoders.Rent(
            item,
            path,
            stream,
            DecoderRole.Playhead,
            project.Settings.FrameRate,
            lane);

        int bitDepth = BitDepthOf(item, stream);
        lease.Frames.Flush(gop.Start);

        int kept = 0;
        while (true)
        {
            using VideoFrame? frame = lease.Frames.ReadFrame();
            if (frame is null || frame.Pts >= gop.End)
            {
                break;
            }

            var key = new FrameKey(item.Hash, stream, Snap(frame.Pts, rate));
            if (!_cache.Contains(key))
            {
                _cache.Add(key, Store(frame, bitDepth));
                kept++;
            }
        }

        // The whole group has to stay put until it has been played through, or the frames at the
        // far end are evicted by the ones at this end before anyone sees them.
        _cache.Pin(item.Hash, stream, gop);

        _log.Debug("Primed {Frames} frames of the group at {Start} for reverse play", kept, gop.Start);
        return kept;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _decoders.FellBack -= OnFellBack;

        if (_ownsDecoders)
        {
            _decoders.Dispose();
        }

        _cache.Dispose();

        foreach (PixelConverter converter in _converters.Values)
        {
            converter.Dispose();
        }

        _converters.Clear();
    }

    /// <summary>
    /// The item actually decoded for a media item and one of its streams: a proxy's stand-in and
    /// its only stream when there is a substitute, the item itself otherwise.
    /// </summary>
    public (MediaItem Item, int Stream, bool IsProxy) Decodable(MediaItem item, int streamIndex)
    {
        ArgumentNullException.ThrowIfNull(item);

        // A proxy's stand-in has one stream, index 0; see ProxyService.
        return Substitute?.Invoke(item) is { } proxy
            ? (proxy, 0, true)
            : (item, streamIndex, false);
    }

    /// <summary>Turns a decoder falling back into something every surface can show.</summary>
    private void OnFellBack(object? sender, DecoderFellBackEventArgs fallback) =>
        _notices?.Report(
            fallback.MediaId,
            fallback.Name,
            DiagnosticCodes.DecoderFellBack,
            $"{fallback.Codec} is decoding on the CPU rather than the GPU. {fallback.Reason}",
            DiagnosticLevel.Information);

    /// <summary>
    /// Where a media item's file is on this machine.
    /// </summary>
    /// <remarks>
    /// A project that has never been saved has no path to be relative to, and its media paths are
    /// absolute because that is all they could have been. Asking the resolver to make sense of an
    /// empty project path throws, which is right for a load and wrong here.
    /// </remarks>
    private static string ResolveMedia(string projectPath, MediaItem item) =>
        projectPath.Length == 0
            ? item.RelativePath
            : ProjectPaths.Resolve(projectPath, item.RelativePath);

    /// <summary>A stream's frame rate, from what import recorded about it.</summary>
    private static Rational RateOf(MediaItem item, int streamIndex)
    {
        if (item.Info?.Streams.FirstOrDefault(stream => stream.Index == streamIndex)?.FrameRate is { } rate
            && !rate.IsZero)
        {
            return rate;
        }

        return item.Sequence?.FrameRate ?? Rational.Fps30;
    }

    /// <summary>A stream's bit depth, which decides whether a hardware frame is NV12 or P010.</summary>
    private static int BitDepthOf(MediaItem item, int streamIndex) =>
        item.Info?.Streams.FirstOrDefault(stream => stream.Index == streamIndex)?.BitDepth is > 0 and int depth
            ? depth
            : 8;

    private static Flicks Snap(Flicks time, Rational rate) =>
        rate.IsZero ? time : Flicks.FromFrames(time.ToFrames(rate, RoundingMode.Nearest), rate);

    /// <summary>Seeks to a source time, caches what comes out, and returns it.</summary>
    private FrameTexture? DecodeTo(
        DecoderLease lease,
        MediaItem item,
        FrameKey key,
        Flicks sourceTime,
        Rational rate,
        int bitDepth,
        SeekMode mode)
    {
        if (mode == SeekMode.Nearest)
        {
            // Shuttling and coarse dragging: the keyframe at or before the target, with nothing
            // decoded forward. It is the wrong frame by up to a group of pictures, which is the
            // trade a shuttle makes, and it lands in single digit milliseconds whatever the file.
            // It goes through the seeker rather than the conform chain, because a conform stage
            // needs consecutive frames and a shuttle does not give it any.
            using VideoFrame? nearest = lease.Seeker.Seek(sourceTime, SeekMode.Nearest);
            if (nearest is null)
            {
                return null;
            }

            Decoded++;
            FrameTexture coarse = Store(nearest, bitDepth);
            _cache.Add(key with { Pts = Snap(nearest.Pts, rate) }, coarse);
            return coarse;
        }

        lease.Frames.Flush(sourceTime);

        using VideoFrame? frame = lease.Frames.ReadFrame();
        if (frame is null)
        {
            return null;
        }

        Decoded++;

        // What came back is the frame displayed at the requested time, which is not always at
        // exactly the requested timestamp: a variable source has no frame there at all. Cache it
        // under what was asked for, so asking again is a hit.
        FrameTexture stored = Store(frame, bitDepth);
        _cache.Add(key, stored);

        Flicks actual = Snap(frame.Pts, rate);
        if (actual != key.Pts && !_cache.Contains(key with { Pts = actual }))
        {
            // And under where it really is, so a neighbouring request finds it too.
            _cache.Add(key with { Pts = actual }, Store(frame, bitDepth));
        }

        return stored;
    }

    /// <summary>Decodes a few frames past the request so the next one is already there.</summary>
    private void ReadAhead(
        DecoderLease lease,
        MediaItem item,
        int streamIndex,
        Rational rate,
        PlayDirection direction,
        int bitDepth)
    {
        if (direction != PlayDirection.Forward || DecodeAhead <= 0)
        {
            return;
        }

        for (int ahead = 0; ahead < DecodeAhead; ahead++)
        {
            using VideoFrame? frame = lease.Frames.ReadFrame();
            if (frame is null)
            {
                return;
            }

            var key = new FrameKey(item.Hash, streamIndex, Snap(frame.Pts, rate));
            if (!_cache.Contains(key))
            {
                _cache.Add(key, Store(frame, bitDepth));
            }
        }
    }

    /// <summary>A converter to RGBA for frames of one size and format, made once and kept.</summary>
    private PixelConverter Converter(VideoFrame frame)
    {
        var key = (frame.Width, frame.Height, frame.PixelFormat);
        if (!_converters.TryGetValue(key, out PixelConverter? converter))
        {
            converter = new PixelConverter(frame.Width, frame.Height, frame.PixelFormat, PixelConverter.RgbaFor(frame.PixelFormat));
            _converters[key] = converter;
        }

        return converter;
    }

    /// <summary>
    /// Puts a decoded frame into a texture the cache can keep, by whichever of the two routes the
    /// decode path calls for.
    /// </summary>
    private FrameTexture Store(VideoFrame frame, int bitDepth)
    {
        if (frame.Location == FrameLocation.Gpu)
        {
            // A hardware frame reports itself as a Direct3D surface rather than as a pixel
            // format, so what it holds is decided by the stream's bit depth: NV12 for eight bit,
            // P010 for anything deeper.
            PixelLayout layout = bitDepth > 8 ? PixelLayout.P010 : PixelLayout.Nv12;

            FrameTexture target = _cache.Textures.Rent(layout, frame.Width, frame.Height, FrameTextureUsage.Copy);
            _copier.Copy(frame.Texture, frame.TextureIndex, target);
            target.Pts = frame.Pts;
            target.Duration = frame.Duration;
            return target;
        }

        // A format nothing samples as it is (a PNG's rgb24, a JPEG's yuvj420p, a palette) is
        // turned into RGBA here, on the CPU, where the frame already is.
        if (PixelLayout.ForName(frame.PixelFormatName) is null)
        {
            using VideoFrame converted = Converter(frame).Convert(frame);
            return Store(converted, bitDepth);
        }

        PixelLayout software = PixelLayout.ForName(frame.PixelFormatName)!;
        FrameTexture uploaded = _cache.Textures.Rent(software, frame.Width, frame.Height, FrameTextureUsage.Upload);

        Span<SourcePlane> planes = stackalloc SourcePlane[software.PlaneCount];
        for (int plane = 0; plane < software.PlaneCount; plane++)
        {
            PixelPlane description = software.Planes[plane];
            planes[plane] = new SourcePlane(
                frame.PlanePointer(plane),
                frame.GetStride(plane),
                description.WidthFor(frame.Width),
                description.HeightFor(frame.Height));
        }

        _uploader.Upload(uploaded, planes);
        uploaded.Pts = frame.Pts;
        uploaded.Duration = frame.Duration;
        return uploaded;
    }

}
