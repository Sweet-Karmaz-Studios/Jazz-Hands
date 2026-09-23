using System.Diagnostics;
using JazzHands.Core.Diagnostics;
using JazzHands.Core.Model;
using JazzHands.Core.Queries;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Engine.Diagnostics;
using JazzHands.Media.Decode;
using JazzHands.Media.Import;
using JazzHands.Render;
using JazzHands.Render.Color;
using JazzHands.Render.Compositing;
using JazzHands.Render.Frames;
using Serilog;
using Vortice.Direct3D11;

namespace JazzHands.Engine.Frames;

/// <summary>How the playhead is moving, which decides how pictures are fetched.</summary>
/// <param name="Playing">True while playback runs.</param>
/// <param name="Rate">The rate while playing: 1 normal, negative backwards.</param>
public readonly record struct Motion(bool Playing, double Rate)
{
    /// <summary>Parked on a frame.</summary>
    public static Motion Still { get; } = new(false, 0.0);
}

/// <summary>
/// Renders a sequence at a moment: the render graph built from the project, each layer's picture
/// fetched from the decoders, and the whole stack composited.
/// </summary>
/// <remarks>
/// This is <c>FrameServer.RenderFrame</c> from the architecture: the preview calls it every frame,
/// and export, thumbnails, <c>jazz frame</c> and MCP's <c>render_frame</c> will call it the same
/// way. It is the engine's side of the render graph: <see cref="RenderGraphBuilder"/> in Render
/// decides what is on screen, and asks this, through <see cref="IFrameProvider"/>, for the decoded
/// pictures it cannot fetch itself.
///
/// How a picture is fetched depends on <see cref="Motion"/>. Parked, it is the exact frame.
/// Playing forwards up to twice normal speed, the playhead decoder runs ahead of it. Backwards, the
/// group of pictures is decoded forwards and kept. Past twice normal speed a shuttle shows
/// keyframes, because every frame would mean decoding sixty times faster than real time. Each
/// layer asks on its own lane, so two layers of one file do not share a decoder.
///
/// A file that cannot be read is reported once and left out of the frame until the project
/// changes, rather than retried at sixty a second.
///
/// Thread affine: it owns a frame server, a compositor and their decoders, caches and passes.
/// </remarks>
public sealed class FrameServer : IFrameProvider, IDisposable
{
    private readonly ILogger _log = Log.ForContext<FrameServer>();
    private readonly SourceFrameServer _sources;
    private readonly DiagnosticsLog? _notices;
    private readonly CacheManager? _cacheManager;
    private readonly Dictionary<(string Hash, int Stream), KeyframeIndex?> _keyframes = [];
    private readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal);
    private string _projectPath = string.Empty;
    private bool _disposed;

    /// <summary>Creates a frame server on a device, with its own decoders, cache and compositor.</summary>
    /// <param name="device">The device.</param>
    /// <param name="hardware">A hardware decode context on the same device, or null for software decode.</param>
    /// <param name="frameCacheBytes">What decoded frames may hold in video memory.</param>
    /// <param name="notices">Where fallbacks and unreadable files are reported.</param>
    /// <param name="cacheManager">Where keyframe indexes are kept between runs.</param>
    public FrameServer(
        RenderDevice device,
        HardwareDeviceContext? hardware = null,
        long frameCacheBytes = FrameCache.DefaultBudgetBytes,
        DiagnosticsLog? notices = null,
        CacheManager? cacheManager = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        Device = device;
        _notices = notices;
        _cacheManager = cacheManager;
        _sources = new SourceFrameServer(
            new DecoderPool(hardware),
            new FrameCache(new FrameTexturePool(device), frameCacheBytes),
            device,
            notices);
        Compositor = new Compositor(device);
    }

    /// <summary>The device.</summary>
    public RenderDevice Device { get; }

    /// <summary>The compositor, whose pool the caller returns rendered stacks to.</summary>
    public Compositor Compositor { get; }

    /// <summary>The decoded picture side, for diagnostics.</summary>
    public SourceFrameServer Sources => _sources;

    /// <summary>How the playhead is moving. Set before each render.</summary>
    public Motion Motion { get; set; } = Motion.Still;

    /// <summary>
    /// Renders a sequence at a time into a stack of premultiplied linear light.
    /// </summary>
    /// <returns>A target from <see cref="Compositor"/>'s pool; the caller returns it.</returns>
    public RenderTarget Render(Project project, Sequence sequence, Flicks time, RenderOptions options, string projectPath = "")
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _projectPath = projectPath;
        RenderGraph graph = RenderGraphBuilder.Build(project, sequence, time, this, options);
        return Compositor.Render(graph);
    }

    /// <summary>
    /// Renders a sequence at a time and encodes it into a target, which is what the preview and a
    /// still export want.
    /// </summary>
    public void Render(
        Project project,
        Sequence sequence,
        Flicks time,
        RenderOptions options,
        ID3D11RenderTargetView target,
        int width,
        int height,
        OutputSettings output,
        string projectPath = "")
    {
        RenderTarget stack = Render(project, sequence, time, options, projectPath);
        try
        {
            Compositor.Output(stack, target, width, height, output);
        }
        finally
        {
            Compositor.Pool.Return(stack);
        }
    }

    /// <summary>
    /// Decodes the frames every visible media layer will need after <paramref name="frame"/>,
    /// until a deadline, so the next renders find them in the cache.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="sequence">The sequence playing.</param>
    /// <param name="frame">The sequence frame on screen now.</param>
    /// <param name="step">How many sequence frames each rendered frame advances: the rounded rate.</param>
    /// <param name="frames">How many frames ahead to fill.</param>
    /// <param name="deadline">A <see cref="Stopwatch"/> timestamp to stop by.</param>
    /// <param name="interrupted">Returns true when something more urgent came in; decoding stops.</param>
    /// <param name="projectPath">Where the project lives.</param>
    /// <returns>False when interrupted before finishing.</returns>
    public bool DecodeAhead(
        Project project,
        Sequence sequence,
        long frame,
        int step,
        int frames,
        long deadline,
        Func<bool> interrupted,
        string projectPath = "")
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(interrupted);

        Rational fps = project.SettingsFor(sequence).FrameRate;

        for (int ahead = 1; ahead <= frames && Stopwatch.GetTimestamp() < deadline; ahead++)
        {
            if (interrupted())
            {
                return false;
            }

            Flicks time = Flicks.FromFrames(frame + (ahead * step), fps);

            foreach (Track track in sequence.Tracks)
            {
                // A reversed clip is primed a group at a time when it is rendered; decoding ahead
                // of it a frame at a time would be a seek per frame.
                if (track.Kind != TrackKind.Video || track.Muted
                    || TimelineQueries.ClipAt(track, time) is not { Enabled: true, Reverse: false, MediaId: { } mediaId } clip
                    || project.MediaItem(mediaId) is not { } item
                    || _failed.ContainsKey(item.Id))
                {
                    continue;
                }

                try
                {
                    _sources.GetSourceFrame(project, clip, time, projectPath, PlayDirection.Forward, SeekMode.Exact, track.Order);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // The render of that frame meets the same error and reports it properly.
                    _log.Debug(exception, "Decoding ahead failed at {Time}", time);
                }
            }
        }

        return true;
    }

    /// <summary>Forgets which files failed, so they are tried again. Call when the project changes.</summary>
    public void Retry() => _failed.Clear();

    /// <inheritdoc />
    SourceFrame? IFrameProvider.Frame(Project project, Clip clip, Flicks timelineTime, int lane)
    {
        if (clip.MediaId is not { } mediaId || project.MediaItem(mediaId) is not { } item || _failed.ContainsKey(item.Id))
        {
            return null;
        }

        try
        {
            if (Fetch(project, clip, item, timelineTime, lane) is not { } frame)
            {
                return null;
            }

            return new SourceFrame(frame, ColorSpaceFor(item, clip.SourceStreamIndex, frame.Layout), Identity(item, clip, frame));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _failed[item.Id] = exception.Message;
            _log.Error(exception, "Could not show {Media}", item.Name);
            _notices?.Report(
                item.Id,
                item.Name,
                DiagnosticCodes.DecodeFailed,
                $"This file could not be shown: {exception.Message}",
                DiagnosticLevel.Error);
            return null;
        }
    }

    /// <summary>The colour signalling of a picture, from what import recorded and what the frame is.</summary>
    /// <remarks>
    /// The probe records whether a stream is HDR but not its matrix, so video falls back on the
    /// convention every player uses: BT.2020 with PQ for HDR, BT.601 up to 576 lines, BT.709
    /// otherwise, limited range. Stills are RGB: sRGB, or linear for float EXR.
    /// </remarks>
    internal static YuvColorSpace ColorSpaceFor(MediaItem item, int streamIndex, PixelLayout layout)
    {
        if (layout.IsRgb)
        {
            return YuvColorSpace.Rgb(layout == PixelLayout.Gbrapf32 ? TransferFunction.Linear : TransferFunction.Srgb);
        }

        MediaStream? stream = item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == streamIndex);

        if (stream?.IsHdr == true)
        {
            return YuvColorSpace.From("bt2020nc", "smpte2084", isFullRange: false, layout.BitDepth);
        }

        string matrix = stream is { Height: > 0 and <= 576 } ? "bt601" : "bt709";
        return YuvColorSpace.From(matrix, "bt709", isFullRange: false, layout.BitDepth);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Compositor.Dispose();
        _sources.Dispose();
    }

    private static string Identity(MediaItem item, Clip clip, FrameTexture frame) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{item.Hash}:{clip.SourceStreamIndex}:{frame.Pts.Value}");

    /// <summary>Gets a clip's picture the way the current motion needs it.</summary>
    private FrameTexture? Fetch(Project project, Clip clip, MediaItem item, Flicks time, int lane)
    {
        Motion motion = Motion;
        SeekMode mode = motion.Playing && Math.Abs(motion.Rate) > 2.0 ? SeekMode.Nearest : SeekMode.Exact;
        PlayDirection direction = !motion.Playing
            ? PlayDirection.Still
            : (motion.Rate > 0) != clip.Reverse ? PlayDirection.Forward : PlayDirection.Reverse;

        if (direction == PlayDirection.Reverse && mode == SeekMode.Exact)
        {
            // Backwards through the source: decode the group the frame is in forwards and keep
            // all of it, which is the only way there is. The next frames are then cache hits.
            if (_sources.GetCachedFrame(project, clip, time) is { } cached)
            {
                return cached;
            }

            if (KeyframesFor(item, clip.SourceStreamIndex) is { } index)
            {
                _sources.PrimeGop(project, clip, time, index, _projectPath, lane);
            }

            return _sources.GetSourceFrame(project, clip, time, _projectPath, PlayDirection.Still, SeekMode.Exact, lane);
        }

        return _sources.GetSourceFrame(project, clip, time, _projectPath, direction, mode, lane);
    }

    /// <summary>A source's keyframe index, from the cache database or built by a scan.</summary>
    private KeyframeIndex? KeyframesFor(MediaItem item, int streamIndex)
    {
        var key = (item.Hash, streamIndex);
        if (_keyframes.TryGetValue(key, out KeyframeIndex? known))
        {
            return known;
        }

        KeyframeIndex? index = null;
        try
        {
            index = _cacheManager is null ? null : KeyframeIndex.Load(_cacheManager, item.Hash, streamIndex);

            if (index is null)
            {
                string path = _projectPath.Length == 0 ? item.RelativePath : ProjectPaths.Resolve(_projectPath, item.RelativePath);
                long started = Stopwatch.GetTimestamp();
                index = KeyframeIndex.Build(path, streamIndex);
                _log.Information(
                    "Indexed {Count} keyframes of {Media} for reverse play in {Ms:F0} ms",
                    index.Count,
                    item.Name,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                if (_cacheManager is not null)
                {
                    index.Save(_cacheManager, item.Hash, streamIndex);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Media.Interop.FfmpegException)
        {
            // Reverse play then seeks for every frame, which is slow but still right.
            _log.Warning(exception, "Could not index the keyframes of {Media}; reverse play will seek for every frame", item.Name);
        }

        _keyframes[key] = index;
        return index;
    }
}
