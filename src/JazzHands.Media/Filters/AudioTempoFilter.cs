using FFmpeg.AutoGen;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Filters;

/// <summary>
/// Changes the speed of planar float audio without changing its pitch.
/// </summary>
/// <remarks>
/// Shuttling at one and a half times normal speed should sound like people talking quickly, not
/// like chipmunks. FFmpeg has two filters that do this. <c>rubberband</c>, a binding to the Rubber
/// Band library, is the better one: phase vocoder with transient detection, clean from a quarter
/// to twice normal speed. It is only there when the build was configured with it, which the pinned
/// GPL build is. <c>atempo</c> is always there and is a plain overlap-add, which smears transients
/// but keeps the pitch; it is the fallback.
///
/// Samples go in and come out as planar float at one rate and channel count, with no conversion
/// either side. Output lags input by the filter's own latency, a few tens of milliseconds for
/// rubberband, which matters for a shuttle not at all.
///
/// Not thread safe: one thread sends and receives.
/// </remarks>
public sealed unsafe class AudioTempoFilter : IDisposable
{
    private static readonly Lazy<bool> RubberbandPresent = new(() =>
    {
        FfmpegLoader.Initialize();
        return ffmpeg.avfilter_get_by_name("rubberband") is not null;
    });

    private readonly ILogger _log = Log.ForContext<AudioTempoFilter>();
    private readonly int _channels;
    private AVFilterGraph* _graph;
    private AVFilterContext* _source;
    private AVFilterContext* _sink;
    private AVFrame* _pending;
    private int _pendingOffset;
    private long _sent;
    private bool _disposed;

    /// <summary>Builds the filter.</summary>
    /// <param name="sampleRate">The rate of what goes in and what comes out.</param>
    /// <param name="channels">The channel count, one to eight.</param>
    /// <param name="tempo">How much faster: 1.5 plays a second of input in two thirds of a second.</param>
    /// <param name="preferRubberband">False to use atempo even where rubberband is available.</param>
    public AudioTempoFilter(int sampleRate, int channels, double tempo, bool preferRubberband = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);

        if (!(tempo >= MinTempo && tempo <= MaxTempo))
        {
            throw new ArgumentOutOfRangeException(nameof(tempo), tempo, $"A tempo runs from {MinTempo} to {MaxTempo}.");
        }

        FfmpegLoader.Initialize();

        SampleRate = sampleRate;
        _channels = channels;
        Tempo = tempo;
        Engine = preferRubberband && HasRubberband ? "rubberband" : "atempo";

        Build();
    }

    /// <summary>The slowest tempo either filter handles cleanly.</summary>
    public const double MinTempo = 0.25;

    /// <summary>The fastest. Past this a shuttle is silent: nobody follows speech at three times.</summary>
    public const double MaxTempo = 2.0;

    /// <summary>True when this FFmpeg build has the rubberband filter.</summary>
    public static bool HasRubberband => RubberbandPresent.Value;

    /// <summary>The sample rate in and out.</summary>
    public int SampleRate { get; }

    /// <summary>The speed factor.</summary>
    public double Tempo { get; }

    /// <summary>Which filter is doing the work: rubberband or atempo.</summary>
    public string Engine { get; }

    /// <summary>Feeds planar samples in.</summary>
    /// <param name="planes">One array per channel, each at least <paramref name="frames"/> long.</param>
    /// <param name="offset">Where in each array to start.</param>
    /// <param name="frames">How many samples per channel.</param>
    public void Send(float[][] planes, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frames <= 0)
        {
            return;
        }

        AVFrame* frame = Av.CheckAlloc(ffmpeg.av_frame_alloc(), "av_frame_alloc");
        try
        {
            frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            frame->sample_rate = SampleRate;
            frame->nb_samples = frames;
            ffmpeg.av_channel_layout_default(&frame->ch_layout, _channels);
            Av.Check(ffmpeg.av_frame_get_buffer(frame, 0), "av_frame_get_buffer");

            for (int channel = 0; channel < _channels; channel++)
            {
                new ReadOnlySpan<float>(planes[channel], offset, frames)
                    .CopyTo(new Span<float>(frame->extended_data[(uint)channel], frames));
            }

            frame->pts = _sent;
            _sent += frames;

            Av.Check(ffmpeg.av_buffersrc_add_frame_flags(_source, frame, 0), "av_buffersrc_add_frame_flags", Engine);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    /// <summary>Takes out whatever the filter has ready, up to a limit.</summary>
    /// <param name="planes">One array per channel to write into.</param>
    /// <param name="offset">Where in each array to start.</param>
    /// <param name="capacity">The most samples per channel to write.</param>
    /// <returns>How many samples per channel were written; zero when the filter wants more input.</returns>
    public int Receive(float[][] planes, int offset, int capacity)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int written = 0;

        while (written < capacity)
        {
            if (_pending is null)
            {
                AVFrame* frame = Av.CheckAlloc(ffmpeg.av_frame_alloc(), "av_frame_alloc");
                int result = ffmpeg.av_buffersink_get_frame(_sink, frame);

                if (result == Av.Again || result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == Av.EndOfFile || result == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.av_frame_free(&frame);
                    break;
                }

                if (result < 0)
                {
                    ffmpeg.av_frame_free(&frame);
                    Av.Check(result, "av_buffersink_get_frame", Engine);
                }

                _pending = frame;
                _pendingOffset = 0;
            }

            int available = _pending->nb_samples - _pendingOffset;
            int count = Math.Min(available, capacity - written);

            for (int channel = 0; channel < _channels; channel++)
            {
                new ReadOnlySpan<float>((float*)_pending->extended_data[(uint)channel] + _pendingOffset, count)
                    .CopyTo(new Span<float>(planes[channel], offset + written, count));
            }

            written += count;
            _pendingOffset += count;

            if (_pendingOffset >= _pending->nb_samples)
            {
                AVFrame* done = _pending;
                _pending = null;
                ffmpeg.av_frame_free(&done);
            }
        }

        return written;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_pending is not null)
        {
            AVFrame* pending = _pending;
            _pending = null;
            ffmpeg.av_frame_free(&pending);
        }

        if (_graph is not null)
        {
            AVFilterGraph* graph = _graph;
            _graph = null;
            ffmpeg.avfilter_graph_free(&graph);
        }
    }

    private void Build()
    {
        AVFilterGraph* graph = Av.CheckAlloc(ffmpeg.avfilter_graph_alloc(), "avfilter_graph_alloc");
        AVFilterInOut* inputs = null;
        AVFilterInOut* outputs = null;

        try
        {
            AVChannelLayout layout;
            ffmpeg.av_channel_layout_default(&layout, _channels);
            byte* name = stackalloc byte[64];
            ffmpeg.av_channel_layout_describe(&layout, name, 64);
            string layoutName = Av.ReadString(name) ?? "stereo";

            string arguments = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"sample_rate={SampleRate}:sample_fmt=fltp:channel_layout={layoutName}:time_base=1/{SampleRate}");

            AVFilterContext* source = null;
            Av.Check(
                ffmpeg.avfilter_graph_create_filter(
                    &source,
                    Av.CheckAlloc(ffmpeg.avfilter_get_by_name("abuffer"), "avfilter_get_by_name (abuffer)"),
                    "in",
                    arguments,
                    null,
                    graph),
                "avfilter_graph_create_filter (abuffer)",
                arguments);

            AVFilterContext* sink = null;
            Av.Check(
                ffmpeg.avfilter_graph_create_filter(
                    &sink,
                    Av.CheckAlloc(ffmpeg.avfilter_get_by_name("abuffersink"), "avfilter_get_by_name (abuffersink)"),
                    "out",
                    null,
                    null,
                    graph),
                "avfilter_graph_create_filter (abuffersink)",
                Engine);

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

            // The format filter pins the output to what went in: without it the sink would
            // accept whatever the tempo filter prefers, and rubberband prefers interleaved.
            string chain = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{(Engine == "rubberband" ? $"rubberband=tempo={Tempo:0.######}:pitch=1" : $"atempo={Tempo:0.######}")},aformat=sample_fmts=fltp:sample_rates={SampleRate}:channel_layouts={layoutName}");

            Av.Check(ffmpeg.avfilter_graph_parse_ptr(graph, chain, &inputs, &outputs, null), "avfilter_graph_parse_ptr", chain);
            Av.Check(ffmpeg.avfilter_graph_config(graph, null), "avfilter_graph_config", chain);

            _graph = graph;
            _source = source;
            _sink = sink;
            graph = null;

            _log.Debug("Built the tempo filter {Chain}", chain);
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
}
