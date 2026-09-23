using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;
using JazzHands.Media.Interop;
using JazzHands.Media.Probe;
using Serilog;

namespace JazzHands.Media.Filters;

/// <summary>
/// A libavfilter video graph: frames in one end, frames out the other.
/// </summary>
/// <remarks>
/// Only the handful of things FFmpeg does that the compositor cannot are filtered here:
/// <c>bwdif</c> deinterlacing, and later <c>vidstab</c> and the scale fallbacks. Colour, blur,
/// transforms and transitions are Direct3D work and never come through this class.
///
/// The graph runs on system memory frames, so anything filtered has been decoded in software.
/// That is a deliberate trade for interlaced media, which is rare, usually standard definition,
/// and not worth a hardware deinterlacer's complexity.
///
/// The buffer source is configured in flicks, so a frame's timestamp means the same thing inside
/// the graph as it does outside it and nothing is converted twice.
/// </remarks>
public sealed unsafe class VideoFilterGraph : IDisposable
{
    private readonly ILogger _log = Log.ForContext<VideoFilterGraph>();
    private readonly Rational _frameRate;
    private readonly FramePool _pool = new(8);

    private AVFilterGraph* _graph;
    private AVFilterContext* _source;
    private AVFilterContext* _sink;
    private ColorInfo _color = new("bt709", "bt709", "bt709", false, "left");
    private Rational _outputTimeBase = new(1, (int)Flicks.PerSecond);
    private bool _endOfInput;
    private bool _disposed;

    /// <summary>Creates an unconfigured graph. It is built from the first frame sent through it.</summary>
    /// <param name="description">A filter chain in FFmpeg syntax, for example <c>bwdif=mode=send_frame</c>.</param>
    /// <param name="frameRate">
    /// The input frame rate, which filters like <c>bwdif</c> need to work out what to tell the
    /// rest of the chain about their output rate.
    /// </param>
    public VideoFilterGraph(string description, Rational frameRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (frameRate.IsZero || frameRate.Num < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameRate),
                frameRate.ToString(),
                "The frame rate must be positive.");
        }

        FfmpegLoader.Initialize();
        Description = description;
        _frameRate = frameRate;
    }

    /// <summary>The filter chain this graph runs.</summary>
    public string Description { get; }

    /// <summary>True once the first frame has been seen and the graph built.</summary>
    public bool IsConfigured => _graph is not null;

    /// <summary>
    /// Pushes a frame into the graph. Read it back out with <see cref="Receive"/> until that
    /// returns null: some filters give back more frames than they were given, and some fewer.
    /// </summary>
    /// <remarks>
    /// The pixels are referenced, not copied, and the caller still owns and disposes the frame it
    /// passed in.
    /// </remarks>
    public void Send(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frame.Location != FrameLocation.Cpu)
        {
            throw new InvalidOperationException(
                $"The filter chain {Description} needs frames in system memory. Open the decoder without a " +
                "hardware device, or download the frame first.");
        }

        if (_endOfInput)
        {
            throw new InvalidOperationException("The graph has already been told the input ended.");
        }

        AVFrame* raw = frame.Handle;
        if (_graph is null)
        {
            _color = frame.Color;
            Build(raw);
        }

        raw->pts = frame.Pts.Value;
        raw->duration = frame.Duration.Value;

        Av.Check(
            ffmpeg.av_buffersrc_add_frame_flags(_source, raw, Av.BufferSrcKeepRef),
            "av_buffersrc_add_frame_flags",
            Description);
    }

    /// <summary>
    /// The next filtered frame, which the caller must dispose, or null when the graph needs more
    /// input or has run dry.
    /// </summary>
    public VideoFrame? Receive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_graph is null)
        {
            return null;
        }

        AvFrame frame = _pool.Rent();
        int result = ffmpeg.av_buffersink_get_frame(_sink, frame.Handle);

        if (result == Av.Again || result == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
            result == Av.EndOfFile || result == ffmpeg.AVERROR_EOF)
        {
            _pool.Return(frame);
            return null;
        }

        if (result < 0)
        {
            _pool.Return(frame);
            Av.Check(result, "av_buffersink_get_frame", Description);
        }

        AVFrame* raw = frame.Handle;

        // Not necessarily the time base we fed in: bwdif halves it so it can express a field, and
        // doubles the timestamps to match even when it is emitting whole frames.
        Flicks pts = raw->pts == ffmpeg.AV_NOPTS_VALUE
            ? Flicks.Zero
            : Flicks.FromTimebase(raw->pts, _outputTimeBase);

        Flicks duration = raw->duration > 0
            ? Flicks.FromTimebase(raw->duration, _outputTimeBase)
            : Flicks.FromFrames(1, _frameRate);

        return new VideoFrame(frame, _pool, pts, duration, _color);
    }

    /// <summary>
    /// Tells the graph no more frames are coming, so the ones it is holding come out of
    /// <see cref="Receive"/>.
    /// </summary>
    public void SignalEndOfInput()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_endOfInput)
        {
            return;
        }

        _endOfInput = true;

        if (_graph is null)
        {
            return;
        }

        Av.Check(
            ffmpeg.av_buffersrc_add_frame_flags(_source, null, 0),
            "av_buffersrc_add_frame_flags (drain)",
            Description);
    }

    /// <summary>
    /// Throws away everything in the graph, for a seek.
    /// </summary>
    /// <remarks>
    /// The graph is torn down rather than emptied, because libavfilter has no flush and a filter
    /// like <c>bwdif</c> holds neighbouring frames it would otherwise blend across the cut. It is
    /// rebuilt from the next frame sent.
    /// </remarks>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FreeGraph();
        _endOfInput = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FreeGraph();
        _pool.Dispose();
    }

    private void Build(AVFrame* frame)
    {
        AVFilterGraph* graph = Av.CheckAlloc(ffmpeg.avfilter_graph_alloc(), "avfilter_graph_alloc");
        AVFilterInOut* inputs = null;
        AVFilterInOut* outputs = null;

        try
        {
            int aspectNum = frame->sample_aspect_ratio.num > 0 ? frame->sample_aspect_ratio.num : 1;
            int aspectDen = frame->sample_aspect_ratio.den > 0 ? frame->sample_aspect_ratio.den : 1;

            string arguments =
                $"video_size={frame->width}x{frame->height}:pix_fmt={frame->format}:" +
                $"time_base=1/{Flicks.PerSecond}:pixel_aspect={aspectNum}/{aspectDen}:" +
                $"frame_rate={_frameRate.Num}/{_frameRate.Den}";

            AVFilterContext* source = null;
            Av.Check(
                ffmpeg.avfilter_graph_create_filter(
                    &source,
                    Av.CheckAlloc(ffmpeg.avfilter_get_by_name("buffer"), "avfilter_get_by_name (buffer)"),
                    "in",
                    arguments,
                    null,
                    graph),
                "avfilter_graph_create_filter (buffer)",
                arguments);

            AVFilterContext* sink = null;
            Av.Check(
                ffmpeg.avfilter_graph_create_filter(
                    &sink,
                    Av.CheckAlloc(ffmpeg.avfilter_get_by_name("buffersink"), "avfilter_get_by_name (buffersink)"),
                    "out",
                    null,
                    null,
                    graph),
                "avfilter_graph_create_filter (buffersink)",
                Description);

            // libavfilter names these from the chain's point of view, which is why they read
            // backwards here: what the chain calls its input is the buffer we write into, so it
            // hangs off the outputs list.
            outputs = Av.CheckAlloc(ffmpeg.avfilter_inout_alloc(), "avfilter_inout_alloc");
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = source;
            outputs->pad_idx = 0;
            outputs->next = null;

            inputs = Av.CheckAlloc(ffmpeg.avfilter_inout_alloc(), "avfilter_inout_alloc");
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = sink;
            inputs->pad_idx = 0;
            inputs->next = null;

            Av.Check(
                ffmpeg.avfilter_graph_parse_ptr(graph, Description, &inputs, &outputs, null),
                "avfilter_graph_parse_ptr",
                Description);

            Av.Check(ffmpeg.avfilter_graph_config(graph, null), "avfilter_graph_config", Description);

            _graph = graph;
            _source = source;
            _sink = sink;
            graph = null;

            AVRational outputTimeBase = sink->inputs[0]->time_base;
            _outputTimeBase = outputTimeBase.num > 0 && outputTimeBase.den > 0
                ? new Rational(outputTimeBase.num, outputTimeBase.den)
                : new Rational(1, (int)Flicks.PerSecond);

            _log.Debug(
                "Built filter graph {Description} for {Width}x{Height}, output time base {TimeBase}",
                Description,
                frame->width,
                frame->height,
                _outputTimeBase);
        }
        finally
        {
            if (inputs is not null)
            {
                ffmpeg.avfilter_inout_free(&inputs);
            }

            if (outputs is not null)
            {
                ffmpeg.avfilter_inout_free(&outputs);
            }

            if (graph is not null)
            {
                ffmpeg.avfilter_graph_free(&graph);
            }
        }
    }

    private void FreeGraph()
    {
        if (_graph is null)
        {
            return;
        }

        AVFilterGraph* graph = _graph;
        _graph = null;
        _source = null;
        _sink = null;
        ffmpeg.avfilter_graph_free(&graph);
    }
}
