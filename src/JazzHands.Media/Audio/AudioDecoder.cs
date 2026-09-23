using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Audio;

/// <summary>
/// Decodes one audio stream into planar float at a chosen rate and channel count.
/// </summary>
/// <remarks>
/// The mixer only ever sees planar float at the project rate, so the resampling is part of
/// decoding rather than a stage somebody might forget. A file with three mono streams at 48 kHz
/// and a file with one stereo stream at 44.1 kHz arrive at the graph the same shape.
///
/// Thread-affine, like its demuxer.
/// </remarks>
public sealed unsafe class AudioDecoder : IDisposable
{
    private readonly ILogger _log = Log.ForContext<AudioDecoder>();
    private readonly Demuxer _demuxer;
    private readonly AvCodecContext _codec;
    private readonly FramePoolAdapter _pool = new();
    private readonly Resampler? _resampler;
    private readonly int _streamIndex;
    private readonly Rational _timeBase;
    private bool _flushed;
    private bool _drained;
    private bool _disposed;

    /// <summary>Opens a decoder for one audio stream of an already-open file.</summary>
    /// <param name="demuxer">The demuxer to pull packets from. Not owned.</param>
    /// <param name="streamIndex">The audio stream to decode.</param>
    /// <param name="targetSampleRate">The rate to convert to, or 0 to keep the source's.</param>
    /// <param name="targetChannels">The channel count to convert to, or 0 to keep the source's.</param>
    public AudioDecoder(Demuxer demuxer, int streamIndex, int targetSampleRate = 0, int targetChannels = 0)
    {
        ArgumentNullException.ThrowIfNull(demuxer);

        _demuxer = demuxer;
        _streamIndex = streamIndex;

        AVStream* stream = demuxer.GetStream(streamIndex);
        if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
        {
            throw new ArgumentException($"Stream {streamIndex} of '{demuxer.Path}' is not audio.", nameof(streamIndex));
        }

        _timeBase = new Rational(stream->time_base.num, stream->time_base.den);

        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec is null)
        {
            throw new FfmpegException(
                Av.DecoderNotFound,
                "avcodec_find_decoder",
                $"{ffmpeg.avcodec_get_name(stream->codecpar->codec_id)} in '{demuxer.Path}'");
        }

        CodecName = ffmpeg.avcodec_get_name(stream->codecpar->codec_id) ?? "unknown";
        _codec = new AvCodecContext(codec);
        AVCodecContext* context = _codec.Handle;

        Av.Check(
            ffmpeg.avcodec_parameters_to_context(context, stream->codecpar),
            "avcodec_parameters_to_context",
            demuxer.Path);

        context->pkt_timebase = stream->time_base;
        context->thread_count = 0;

        Av.Check(ffmpeg.avcodec_open2(context, codec, null), "avcodec_open2", demuxer.Path);

        SourceSampleRate = context->sample_rate;
        SourceChannels = context->ch_layout.nb_channels;
        SampleRate = targetSampleRate > 0 ? targetSampleRate : SourceSampleRate;
        Channels = targetChannels > 0 ? targetChannels : SourceChannels;

        bool needsConversion = SampleRate != SourceSampleRate
            || Channels != SourceChannels
            || context->sample_fmt != AVSampleFormat.AV_SAMPLE_FMT_FLTP;

        if (needsConversion)
        {
            _resampler = new Resampler(
                SourceSampleRate,
                SourceChannels,
                context->sample_fmt,
                SampleRate,
                Channels);
        }

        _log.Debug(
            "Opened {Codec} audio decoder for stream {Stream} of {File}: {SourceRate} Hz {SourceChannels}ch to {Rate} Hz {Channels}ch",
            CodecName,
            streamIndex,
            demuxer.Path,
            SourceSampleRate,
            SourceChannels,
            SampleRate,
            Channels);
    }

    /// <summary>The codec short name, for example aac.</summary>
    public string CodecName { get; }

    /// <summary>The rate the file holds.</summary>
    public int SourceSampleRate { get; }

    /// <summary>The channel count the file holds.</summary>
    public int SourceChannels { get; }

    /// <summary>The rate blocks come out at.</summary>
    public int SampleRate { get; }

    /// <summary>The channel count blocks come out with.</summary>
    public int Channels { get; }

    /// <summary>True when the samples are converted on the way out.</summary>
    public bool IsResampling => _resampler is not null;

    /// <summary>Blocks decoded since the decoder was opened.</summary>
    public long BlocksDecoded { get; private set; }

    /// <summary>
    /// The next block, reading packets as needed, or null at the end of the stream.
    /// </summary>
    public AudioFrame? ReadFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        AVCodecContext* context = _codec.Handle;

        while (true)
        {
            AvFrame frame = _pool.Rent();
            int receive = ffmpeg.avcodec_receive_frame(context, frame.Handle);

            if (receive == 0)
            {
                BlocksDecoded++;
                return Deliver(frame);
            }

            _pool.Return(frame);

            if (receive == Av.EndOfFile || receive == ffmpeg.AVERROR_EOF)
            {
                return DrainResampler();
            }

            if (receive != Av.Again && receive != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Av.Check(receive, "avcodec_receive_frame", _demuxer.Path);
            }

            if (_flushed)
            {
                return DrainResampler();
            }

            AVPacket* packet = _demuxer.ReadPacket(_streamIndex);
            if (packet is null)
            {
                Av.Check(ffmpeg.avcodec_send_packet(context, null), "avcodec_send_packet (drain)", _demuxer.Path);
                _flushed = true;
                continue;
            }

            int send = ffmpeg.avcodec_send_packet(context, packet);
            if (send != Av.Again && send != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Av.Check(send, "avcodec_send_packet", _demuxer.Path);
            }
        }
    }

    /// <summary>
    /// Seeks so the next read starts at or before a time, and clears what the decoder was holding.
    /// </summary>
    /// <remarks>
    /// Audio seeking lands on a packet boundary, and a compressed frame decodes to more samples
    /// than the seek asked for, so the caller trims. The decoder is flushed because whatever was
    /// buffered belongs to where it used to be.
    /// </remarks>
    public void SeekTo(Flicks target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _demuxer.SeekToKeyframeBefore(_streamIndex, target);
        ffmpeg.avcodec_flush_buffers(_codec.Handle);
        _flushed = false;
        _drained = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resampler?.Dispose();
        _codec.Dispose();
        _pool.Dispose();
    }

    /// <summary>Converts a decoded frame if it needs it, and works out where it sits in time.</summary>
    private AudioFrame? Deliver(AvFrame frame)
    {
        AVFrame* raw = frame.Handle;

        long stamp = raw->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
            ? raw->best_effort_timestamp
            : raw->pts;

        Flicks pts = stamp == ffmpeg.AV_NOPTS_VALUE
            ? Flicks.Zero
            : Flicks.FromTimebase(stamp, _timeBase);

        if (_resampler is null)
        {
            return new AudioFrame(frame, _pool, pts, SampleRate);
        }

        try
        {
            return _resampler.Convert(raw, pts) ?? ReadFrame();
        }
        finally
        {
            _pool.Return(frame);
        }
    }

    /// <summary>
    /// The samples the rate converter is still holding at the end of the stream.
    /// </summary>
    /// <remarks>
    /// Without this the last few milliseconds of every resampled clip are lost inside the filter,
    /// which on a cut is an audible click rather than a silence nobody notices.
    /// </remarks>
    private AudioFrame? DrainResampler()
    {
        if (_resampler is null || _drained)
        {
            return null;
        }

        _drained = true;
        return _resampler.Drain(Flicks.Zero);
    }
}
