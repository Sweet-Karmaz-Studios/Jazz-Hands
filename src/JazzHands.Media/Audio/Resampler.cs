using FFmpeg.AutoGen;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Audio;

/// <summary>
/// Converts decoded audio to one sample rate, one channel count and planar float.
/// </summary>
/// <remarks>
/// Everything in the graph runs at the project's rate in planar float, so this is where the
/// variety stops: a 44.1 kHz stereo AAC stream and a 48 kHz mono one both arrive at the mixer as
/// the same shape. Doing it here rather than per node means the rate conversion filter runs once
/// per block rather than once per stage.
///
/// Kaiser windowed sinc, as the audio-engine skill sets. It costs more than the default and the
/// difference is audible on a 44.1 to 48 conversion, which is most of what a game capture folder
/// contains.
///
/// Thread affine, like everything else wrapping an FFmpeg context.
/// </remarks>
public sealed unsafe class Resampler : IDisposable
{
    private readonly ILogger _log = Log.ForContext<Resampler>();
    private readonly FramePoolAdapter _pool = new();
    private SwrContext* _context;
    private bool _disposed;

    /// <summary>Creates a converter between two formats.</summary>
    /// <param name="sourceRate">The rate samples arrive at.</param>
    /// <param name="sourceChannels">How many channels arrive.</param>
    /// <param name="sourceFormat">The sample format they arrive in.</param>
    /// <param name="targetRate">The rate to convert to.</param>
    /// <param name="targetChannels">How many channels to convert to.</param>
    public Resampler(
        int sourceRate,
        int sourceChannels,
        AVSampleFormat sourceFormat,
        int targetRate,
        int targetChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceChannels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetChannels);

        FfmpegLoader.Initialize();

        SourceRate = sourceRate;
        SourceChannels = sourceChannels;
        TargetRate = targetRate;
        TargetChannels = targetChannels;

        AVChannelLayout inLayout;
        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&inLayout, sourceChannels);
        ffmpeg.av_channel_layout_default(&outLayout, targetChannels);

        SwrContext* context = null;
        int result = ffmpeg.swr_alloc_set_opts2(
            &context,
            &outLayout,
            AVSampleFormat.AV_SAMPLE_FMT_FLTP,
            targetRate,
            &inLayout,
            sourceFormat,
            sourceRate,
            0,
            null);

        if (result < 0 || context is null)
        {
            throw new FfmpegException(result, "swr_alloc_set_opts2", $"{sourceRate} Hz to {targetRate} Hz");
        }

        _context = context;

        // Kaiser windowed sinc rather than the default, which is audibly softer on the 44.1 to 48
        // conversion that most captured audio needs.
        ffmpeg.av_opt_set_int(_context, "filter_type", (long)SwrFilterType.SWR_FILTER_TYPE_KAISER, 0);

        try
        {
            Av.Check(ffmpeg.swr_init(_context), "swr_init");
        }
        catch
        {
            SwrContext* dying = _context;
            _context = null;
            ffmpeg.swr_free(&dying);
            throw;
        }

        _log.Debug(
            "Resampling {SourceRate} Hz {SourceChannels}ch {Format} to {TargetRate} Hz {TargetChannels}ch planar float",
            sourceRate,
            sourceChannels,
            sourceFormat,
            targetRate,
            targetChannels);
    }

    /// <summary>The rate samples arrive at.</summary>
    public int SourceRate { get; }

    /// <summary>How many channels arrive.</summary>
    public int SourceChannels { get; }

    /// <summary>The rate samples leave at.</summary>
    public int TargetRate { get; }

    /// <summary>How many channels leave.</summary>
    public int TargetChannels { get; }

    /// <summary>True when nothing has to be converted, so a caller can skip this entirely.</summary>
    public bool IsPassThrough { get; init; }

    /// <summary>
    /// Converts one decoded frame.
    /// </summary>
    /// <remarks>
    /// The output frame count is not the input count: a rate conversion has a filter delay, so
    /// early calls come back short and the tail comes out of <see cref="Drain"/>. A caller that
    /// assumed one in one out would drop the first few milliseconds of every clip.
    /// </remarks>
    /// <returns>The converted samples, which the caller disposes, or null when none came out yet.</returns>
    internal AudioFrame? Convert(AVFrame* source, Core.Time.Flicks pts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int available = (int)ffmpeg.swr_get_out_samples(_context, source is null ? 0 : source->nb_samples);
        if (available <= 0)
        {
            return null;
        }

        AvFrame output = _pool.Rent();

        try
        {
            AVFrame* raw = output.Handle;
            raw->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            raw->sample_rate = TargetRate;
            raw->nb_samples = available;
            ffmpeg.av_channel_layout_default(&raw->ch_layout, TargetChannels);

            Av.Check(ffmpeg.av_frame_get_buffer(raw, 0), "av_frame_get_buffer");

            int written = ffmpeg.swr_convert(
                _context,
                raw->extended_data,
                available,
                source is null ? null : source->extended_data,
                source is null ? 0 : source->nb_samples);

            Av.Check(written, "swr_convert");

            if (written == 0)
            {
                _pool.Return(output);
                return null;
            }

            raw->nb_samples = written;
            return new AudioFrame(output, _pool, pts, TargetRate);
        }
        catch
        {
            _pool.Return(output);
            throw;
        }
    }

    /// <summary>
    /// Whatever the filter is still holding, at the end of a stream.
    /// </summary>
    /// <remarks>
    /// A rate conversion keeps samples in flight, so the last block of a clip is inside the
    /// resampler rather than in the decoder. Without this the end of every clip is a few
    /// milliseconds short, which on a cut is an audible click.
    /// </remarks>
    public AudioFrame? Drain(Core.Time.Flicks pts) => Convert(null, pts);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_context is not null)
        {
            SwrContext* dying = _context;
            _context = null;
            ffmpeg.swr_free(&dying);
        }

        _pool.Dispose();
    }
}
