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
    private readonly Dictionary<string, bool> _missing = new(StringComparer.Ordinal);
    private string _projectPath = string.Empty;
    private ID3D11Query? _decodeFence;
    private bool _decodePending;
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
        if (options.ProjectFolder.Length == 0 && projectPath.Length > 0)
        {
            options = options with { ProjectFolder = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty };
        }

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
    /// <remarks>
    /// One frame at a time, and the next decode is issued only once the last one has finished on
    /// the GPU. A decode is cheap to submit and slow to run, especially on a GPU idling at low
    /// clocks through 1x playback, and the next present waits behind every piece of GPU work
    /// submitted before it: queueing five 4K decodes at once made that present 30 ms late. Paced,
    /// a present waits behind one decode at most.
    /// </remarks>
    /// <param name="project">The project.</param>
    /// <param name="sequence">The sequence playing.</param>
    /// <param name="frame">The sequence frame on screen now.</param>
    /// <param name="step">How many sequence frames each rendered frame advances: the rounded rate.</param>
    /// <param name="frames">How many frames ahead to fill.</param>
    /// <param name="deadline">A <see cref="Stopwatch"/> timestamp to stop by.</param>
    /// <param name="interrupted">Returns true when something more urgent came in; decoding stops.</param>
    /// <param name="projectPath">Where the project lives.</param>
    /// <returns>True when every frame asked for is in the cache; false when the deadline or a request came first.</returns>
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

        for (int ahead = 1; ahead <= frames; ahead++)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            if (interrupted())
            {
                return false;
            }

            Flicks time = Flicks.FromFrames(frame + (ahead * step), fps);

            foreach (Track track in sequence.Tracks)
            {
                if (track.Kind != TrackKind.Video || track.Muted)
                {
                    continue;
                }

                // Inside a transition both clips are on screen, each on its own lane, as the
                // graph builder asks for them.
                TrackMoment moment = TransitionTiming.At(track, time, fps);
                if (!DecodeAhead(project, moment.Clip, track.Order, time, deadline, interrupted, projectPath)
                    || !DecodeAhead(project, moment.Incoming, RenderGraphBuilder.IncomingLane(track.Order), time, deadline, interrupted, projectPath))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Decodes one clip's frame at a time ahead, unless it is there already; false when the deadline or a request came first.</summary>
    private bool DecodeAhead(Project project, Clip? candidate, int lane, Flicks time, long deadline, Func<bool> interrupted, string projectPath)
    {
        // A reversed clip is primed a group at a time when it is rendered; decoding ahead of it a
        // frame at a time would be a seek per frame.
        if (candidate is not { Enabled: true, Reverse: false, MediaId: { } mediaId } clip
            || project.MediaItem(mediaId) is not { } item
            || _failed.ContainsKey(item.Id))
        {
            return true;
        }

        time = TransitionTiming.ClampToSource(project, clip, time);
        if (_sources.GetCachedFrame(project, clip, time) is not null)
        {
            return true;
        }

        if (!AwaitLastDecode(deadline, interrupted))
        {
            return false;
        }

        try
        {
            _sources.GetSourceFrame(project, clip, time, projectPath, PlayDirection.Forward, SeekMode.Exact, lane, readAhead: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The render of that frame meets the same error and reports it properly.
            _log.Debug(exception, "Decoding ahead failed at {Time}", time);
        }

        _decodeFence ??= Device.Device.CreateQuery(new QueryDescription { QueryType = QueryType.Event });
        Device.ImmediateContext.End(_decodeFence);
        _decodePending = true;
        return true;
    }

    /// <summary>Waits for the last decode issued ahead to finish on the GPU; false when the deadline or a request came first.</summary>
    private bool AwaitLastDecode(long deadline, Func<bool> interrupted)
    {
        if (_decodeFence is null || !_decodePending)
        {
            return true;
        }

        // GetData without the do-not-flush flag flushes, which is what gets the decode running.
        ID3D11DeviceContext context = Device.ImmediateContext;
        var spin = default(SpinWait);

        while (context.GetData(_decodeFence, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code != 0)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            if (interrupted())
            {
                return false;
            }

            spin.SpinOnce();
        }

        _decodePending = false;
        return true;
    }

    /// <summary>Forgets which files failed or were missing, so they are tried again. Call when the project changes.</summary>
    public void Retry()
    {
        _failed.Clear();
        _missing.Clear();
    }

    /// <inheritdoc />
    string? IFrameProvider.Offline(Project project, Clip clip)
    {
        if (clip.MediaId is not { } mediaId || project.MediaItem(mediaId) is not { } item)
        {
            return null;
        }

        if (!_missing.TryGetValue(item.Id, out bool missing))
        {
            string path = _projectPath.Length == 0 ? Path.GetFullPath(item.RelativePath) : ProjectPaths.Resolve(_projectPath, item.RelativePath);
            missing = item.Kind == MediaKind.ImageSequence ? !Directory.Exists(Path.GetDirectoryName(path)) : !File.Exists(path);
            _missing[item.Id] = missing;
        }

        return missing ? item.Name
            : _failed.ContainsKey(item.Id) ? $"{item.Name} (cannot be read)"
            : null;
    }

    /// <inheritdoc />
    Core.Stabilization.CameraMotion? IFrameProvider.Motion(Project project, Clip clip) =>
        clip.MediaId is { } mediaId && project.MediaItem(mediaId) is { Hash.Length: > 0 } item
            ? Effects.MotionStore.For(_projectPath).Load(item.Hash, clip.SourceStreamIndex)
            : null;

    /// <summary>
    /// What to decode in place of a media item, or null for the item itself: the proxy service's
    /// <see cref="Caching.ProxyService.Substitute"/> for playback, nothing for export.
    /// </summary>
    public Func<MediaItem, MediaItem?>? Substitute
    {
        get => _sources.Substitute;
        set => _sources.Substitute = value;
    }

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

            (MediaItem decoded, int stream, bool proxy) = _sources.Decodable(item, clip.SourceStreamIndex);
            if (!proxy)
            {
                return new SourceFrame(
                    frame,
                    ColorSpaceFor(item, clip.SourceStreamIndex, frame.Layout, ToneMapping.Resolve(clip.ToneMap, project.Settings.ToneMap)),
                    Identity(item.Hash, stream, frame));
            }

            // A proxy is placed as the picture it stands for, at the source's size, and was
            // written BT.709 limited range by the export that made it whatever the source was.
            MediaStream? original = item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == clip.SourceStreamIndex);
            return new SourceFrame(
                frame,
                YuvColorSpace.From("bt709", "bt709", isFullRange: false, frame.Layout.BitDepth),
                Identity(decoded.Hash, stream, frame))
            {
                Width = original?.Width ?? 0,
                Height = original?.Height ?? 0,
            };
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
    /// The stream's own matrix, transfer, primaries and range when import recorded them (Phase
    /// 17). What a file leaves unspecified, and every stream in a project saved before then, falls
    /// back on the convention every player uses: BT.2020 with PQ for HDR, BT.601 up to 576 lines,
    /// BT.709 otherwise, limited range. Stills are RGB: sRGB, or linear for float EXR. HDR is tone
    /// mapped as the clip says, else the project, with the file's own peak unless the clip gives one.
    /// </remarks>
    internal static YuvColorSpace ColorSpaceFor(MediaItem item, int streamIndex, PixelLayout layout, ToneMapping? mapping = null)
    {
        if (layout.IsRgb)
        {
            return YuvColorSpace.Rgb(layout == PixelLayout.Gbrapf32 ? TransferFunction.Linear : TransferFunction.Srgb);
        }

        MediaStream? stream = item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == streamIndex);
        StreamColor? color = stream?.Color;
        bool hdr = color?.IsHdr ?? stream?.IsHdr ?? false;

        string matrix = Stated(color?.Matrix)
            ?? (hdr ? "bt2020nc" : stream is { Height: > 0 and <= 576 } ? "bt601" : "bt709");
        string transfer = Stated(color?.Transfer) ?? (hdr ? "smpte2084" : "bt709");

        YuvColorSpace space = YuvColorSpace.From(matrix, transfer, color?.IsFullRange ?? false, layout.BitDepth, Stated(color?.Primaries) ?? string.Empty);

        ToneMapping tone = mapping ?? ToneMapping.Default;
        double peak = tone.PeakNits ?? color?.PeakNits ?? 1000.0;
        return space with { ToneMap = new ToneMapParameters(tone.Operator, (float)peak, (float)tone.Desaturate) };
    }

    /// <summary>A signalling name, or null when the file did not really say.</summary>
    private static string? Stated(string? name) =>
        name is null or "" or "unknown" or "unspecified" or "reserved" ? null : name;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _decodeFence?.Dispose();
        Compositor.Dispose();
        _sources.Dispose();
    }

    private static string Identity(string hash, int stream, FrameTexture frame) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{hash}:{stream}:{frame.Pts.Value}");

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

            (MediaItem decoded, int stream, _) = _sources.Decodable(item, clip.SourceStreamIndex);
            if (KeyframesFor(decoded, stream) is { } index)
            {
                _sources.PrimeGop(project, clip, time, index, _projectPath, lane);
            }

            return _sources.GetSourceFrame(project, clip, time, _projectPath, PlayDirection.Still, SeekMode.Exact, lane);
        }

        // Playing forwards at up to twice normal speed, the engine decodes ahead at its own pace
        // (see DecodeAhead), so a miss here decodes just the frame and not a burst after it.
        bool paced = direction == PlayDirection.Forward && mode == SeekMode.Exact && Math.Abs(motion.Rate) <= 2.0;
        return _sources.GetSourceFrame(project, clip, time, _projectPath, direction, mode, lane, readAhead: !paced);
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
