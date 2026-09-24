using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;
using JazzHands.Media.Interop;

namespace JazzHands.Media.Thumbnails;

/// <summary>A small picture of a source, as a JPEG.</summary>
/// <param name="FrameTime">The source time of the frame it shows, which can be before the time asked for.</param>
/// <param name="Width">Its width in pixels, square pixels.</param>
/// <param name="Height">Its height.</param>
/// <param name="Jpeg">The file's bytes.</param>
public sealed record Thumbnail(Flicks FrameTime, int Width, int Height, byte[] Jpeg);

/// <summary>
/// Takes thumbnails of one video stream: decode in software, scale down, encode as JPEG.
/// </summary>
/// <remarks>
/// <para>
/// The decode is the cost, and there are two ways to pay less of it. A request carries a
/// tolerance, and when the keyframe at or before the time is inside it that keyframe is the
/// thumbnail: one frame decoded, where an exact frame in the middle of a two second group is
/// sixty. A strip at a coarse zoom is almost all keyframes this way. And a request just after
/// the last one decodes forward from where the decoder is rather than seeking, so a run of close
/// thumbnails costs one pass.
/// </para>
/// <para>
/// Nothing here touches the GPU. Thumbnails are background work beside playback, and a hardware
/// decoder's copy out is GPU work the next present would wait behind; see the Phase 10 notes.
/// </para>
/// <para>
/// The picture is scaled to the height asked for (never up), to square pixels, and converted from
/// the source's matrix and range to the full range BT.601 a JPEG is. HDR sources are not tone
/// mapped: their thumbnails look flat, which is legible and honest about what the file is.
/// </para>
/// <para>Thread affine, like its decoder.</para>
/// </remarks>
public sealed unsafe class ThumbnailExtractor : IDisposable
{
    /// <summary>The height thumbnails are taken at: sharp on a 2x display in a 80 pixel track.</summary>
    public const int DefaultHeight = 160;

    /// <summary>Frames ahead of the last picture that are decoded to rather than looked for at a keyframe.</summary>
    private const int NearlyThere = 4;

    private readonly Demuxer _demuxer;
    private readonly VideoDecoder _decoder;
    private readonly Seeker _seeker;
    private readonly int _height;
    private readonly Rational _frameRate;

    private SwsContext* _scaler;
    private ScalerKey _scalerFor;
    private JpegWriter? _writer;
    private Flicks? _last;
    private Flicks? _keyframe;
    private Flicks _group;
    private bool _disposed;

    /// <summary>Opens a stream to take thumbnails of.</summary>
    /// <param name="path">The file, or a printf pattern for an image sequence.</param>
    /// <param name="streamIndex">The video stream, or -1 for the best one.</param>
    /// <param name="demuxOptions">Options an image sequence needs, or null.</param>
    /// <param name="height">The height to scale to.</param>
    public ThumbnailExtractor(
        string path,
        int streamIndex = -1,
        IReadOnlyDictionary<string, string>? demuxOptions = null,
        int height = DefaultHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 16);

        Path = path;
        _height = height;
        _demuxer = new Demuxer(path, demuxOptions, fileMustExist: !path.Contains('%', StringComparison.Ordinal));

        try
        {
            int stream = streamIndex >= 0 ? streamIndex : _demuxer.FindBestStream(Probe.StreamKind.Video);
            if (stream < 0)
            {
                throw new InvalidOperationException($"'{path}' has no video stream to take thumbnails of.");
            }

            _decoder = new VideoDecoder(_demuxer, stream, hardware: null, poolDepth: 2, DecodeTuning.Thumbnail);
            _seeker = new Seeker(_demuxer, _decoder, stream);
            _frameRate = _decoder.FrameRate;
        }
        catch
        {
            _decoder?.Dispose();
            _demuxer.Dispose();
            throw;
        }
    }

    /// <summary>The file this reads.</summary>
    public string Path { get; }

    /// <summary>Frames decoded so far, a cost counter for the tests and the benchmark.</summary>
    public long FramesDecoded => _decoder.FramesDecoded;

    /// <summary>
    /// A thumbnail of the frame at a time, or of a keyframe no further before it than the
    /// tolerance allows.
    /// </summary>
    /// <param name="time">The source time.</param>
    /// <param name="tolerance">How far before the time the picture may be. Zero for exactly the frame there.</param>
    /// <returns>The thumbnail, or null when the time is past the end of the stream.</returns>
    public Thumbnail? Extract(Flicks time, Flicks tolerance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (time < Flicks.Zero)
        {
            time = Flicks.Zero;
        }

        using VideoFrame? frame = Decode(time, tolerance);
        if (frame is null)
        {
            return null;
        }

        _last = frame.Pts;
        return Encode(frame);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer?.Dispose();

        if (_scaler is not null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        _seeker.Dispose();
        _decoder.Dispose();
        _demuxer.Dispose();
    }

    private VideoFrame? Decode(Flicks time, Flicks tolerance)
    {
        long framesAhead = _last is { } last && time > last
            ? (time - last).ToFrames(_frameRate, RoundingMode.Ceiling)
            : long.MaxValue;

        // Just ahead of the last one, decoding on is cheaper than a seek, and the seeker does that
        // by itself for an exact request inside its forward window. With a tolerance it is only
        // worth it a few frames ahead, or when no keyframe can lie between here and the time (the
        // group the last picture came from runs past it): otherwise a keyframe within the
        // tolerance is one decode where decoding on can be forty.
        bool sameGroup = _keyframe is { } keyframe
            && _group > Flicks.Zero
            && time < keyframe + _group
            && time - keyframe > tolerance;

        bool ahead = tolerance > Flicks.Zero
            ? framesAhead <= NearlyThere || (sameGroup && framesAhead <= _seeker.MaxForwardDecodeFrames)
            : framesAhead <= _seeker.MaxForwardDecodeFrames;

        if (!ahead && tolerance > Flicks.Zero)
        {
            VideoFrame? nearest = _seeker.Seek(time, SeekMode.Nearest);
            if (nearest is null)
            {
                _keyframe = null;
                return _seeker.Seek(time);
            }

            Learn(nearest.Pts);

            // A keyframe can present a frame or two after the time asked for when the stream
            // reorders, which is as good as before it.
            Flicks distance = nearest.Pts <= time ? time - nearest.Pts : nearest.Pts - time;
            if (distance <= tolerance)
            {
                return nearest;
            }

            nearest.Dispose();
        }
        else if (!ahead)
        {
            // An exact seek lands on a keyframe this does not see.
            _keyframe = null;
        }

        return _seeker.Seek(time);
    }

    /// <summary>
    /// Notes a keyframe the seeker landed on, and the shortest distance seen between two, which is
    /// the file's group length as far as anyone can tell without scanning it.
    /// </summary>
    private void Learn(Flicks keyframe)
    {
        if (_keyframe is { } previous && previous != keyframe)
        {
            Flicks apart = keyframe > previous ? keyframe - previous : previous - keyframe;
            _group = _group > Flicks.Zero ? Flicks.Min(_group, apart) : apart;
        }

        _keyframe = keyframe;
    }

    private Thumbnail Encode(VideoFrame frame)
    {
        AVFrame* source = frame.Handle;

        // Square pixels: anamorphic sources are stretched to the width they are shown at.
        double aspect = source->sample_aspect_ratio.num > 0 && source->sample_aspect_ratio.den > 0
            ? (double)source->sample_aspect_ratio.num / source->sample_aspect_ratio.den
            : 1.0;

        int height = Even(Math.Min(_height, frame.Height));
        int width = Even((int)Math.Round(height * frame.Width * aspect / frame.Height, MidpointRounding.AwayFromZero));

        if (_writer is null || _writer.Width != width || _writer.Height != height)
        {
            _writer?.Dispose();
            _writer = new JpegWriter(width, height);
        }

        SwsContext* scaler = Scaler(frame, width, height);
        AVFrame* target = _writer.Frame;

        Av.Check(
            ffmpeg.sws_scale(scaler, source->data, source->linesize, 0, frame.Height, target->data, target->linesize),
            "sws_scale");

        return new Thumbnail(frame.Pts, width, height, _writer.Encode());
    }

    private SwsContext* Scaler(VideoFrame frame, int width, int height)
    {
        var wanted = new ScalerKey(frame.Width, frame.Height, frame.PixelFormat, frame.Color.Matrix, frame.Color.IsFullRange, width, height);

        if (_scaler is not null && _scalerFor == wanted)
        {
            return _scaler;
        }

        if (_scaler is not null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        // Area averaging: a twelve times reduction by bilinear sampling would skip most of the
        // picture and shimmer.
        _scaler = Av.CheckAlloc(
            ffmpeg.sws_getContext(
                frame.Width,
                frame.Height,
                frame.PixelFormat,
                width,
                height,
                AVPixelFormat.AV_PIX_FMT_YUV420P,
                (int)SwsFlags.SWS_AREA,
                null,
                null,
                null),
            $"sws_getContext ({frame.PixelFormatName} to thumbnail)");

        int_array4 from = *(int_array4*)ffmpeg.sws_getCoefficients(MatrixOf(frame.Color.Matrix));
        int_array4 to = *(int_array4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU601);

        // Brightness 0, contrast and saturation 1.0 in 16.16 fixed point. An RGB source ignores
        // the input matrix, which is right.
        ffmpeg.sws_setColorspaceDetails(_scaler, in from, frame.Color.IsFullRange ? 1 : 0, in to, 1, 0, 1 << 16, 1 << 16);

        _scalerFor = wanted;
        return _scaler;
    }

    private static int MatrixOf(string matrix) => matrix switch
    {
        "bt2020nc" or "bt2020c" => ffmpeg.SWS_CS_BT2020,
        "bt470bg" or "smpte170m" or "fcc" => ffmpeg.SWS_CS_ITU601,
        "smpte240m" => ffmpeg.SWS_CS_SMPTE240M,
        _ => ffmpeg.SWS_CS_ITU709,
    };

    private static int Even(int value) => Math.Max(2, value & ~1);

    /// <summary>What a scaler was built for; a stream that changes size mid-way gets a new one.</summary>
    private readonly record struct ScalerKey(
        int SourceWidth,
        int SourceHeight,
        AVPixelFormat Format,
        string Matrix,
        bool FullRange,
        int Width,
        int Height);
}
