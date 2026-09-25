using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Encode;

/// <summary>What an audio encoder is asked for.</summary>
/// <param name="Encoder">The FFmpeg encoder name: aac, libopus, flac, libmp3lame, ac3, eac3, pcm_s16le or pcm_s24le.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">Channel count: 1, 2 or 6.</param>
/// <param name="Bitrate">Bits per second for the lossy encoders, or 0 for the encoder's own default.</param>
public sealed record AudioEncoderSettings(string Encoder, int SampleRate, int Channels, long Bitrate = 256_000);

/// <summary>
/// One FFmpeg audio encoder, fed planar float blocks of any length.
/// </summary>
/// <remarks>
/// The mixer hands out blocks of whatever size it likes; AAC wants exactly 1024 samples a frame.
/// A FIFO between them takes up the difference, and the last frame goes out short, which AAC
/// allows. Timestamps are sample counts, so the stream is exactly as long as what was written.
///
/// Thread affine: open, write and flush on one thread.
/// </remarks>
public sealed unsafe class AudioEncoder : IDisposable
{
    private readonly ILogger _log = Log.ForContext<AudioEncoder>();
    private readonly AvCodecContext _context;
    private readonly AvPacket _packet = new();
    private readonly AvFrame _frame = new();
    private readonly int _frameSize;
    private AVAudioFifo* _fifo;
    private byte[] _scratch = [];
    private long _samplesSent;

    private AudioEncoder(AvCodecContext context, AudioEncoderSettings settings)
    {
        _context = context;
        Settings = settings;
        _frameSize = context.Handle->frame_size > 0 ? context.Handle->frame_size : 1024;
        _fifo = Av.CheckAlloc(
            ffmpeg.av_audio_fifo_alloc(context.Handle->sample_fmt, settings.Channels, _frameSize * 4),
            "av_audio_fifo_alloc");
    }

    /// <summary>What was asked for.</summary>
    public AudioEncoderSettings Settings { get; }

    /// <summary>The time base packets come out in: one sample.</summary>
    public Rational TimeBase => new(1, Settings.SampleRate);

    /// <summary>Samples encoded so far.</summary>
    public long SamplesEncoded => _samplesSent;

    internal AVCodecContext* Handle => _context.Handle;

    /// <summary>Opens the encoder.</summary>
    /// <param name="settings">What to encode.</param>
    /// <param name="globalHeader">True when the container keeps the codec header, as MP4 does.</param>
    public static AudioEncoder Open(AudioEncoderSettings settings, bool globalHeader)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FfmpegLoader.Initialize();

        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(settings.Encoder);
        if (codec is null)
        {
            throw new FfmpegException($"The audio encoder '{settings.Encoder}' is not in this FFmpeg build.");
        }

        var context = new AvCodecContext(codec);
        try
        {
            AVCodecContext* handle = context.Handle;
            handle->sample_rate = settings.SampleRate;
            handle->sample_fmt = ChooseFormat(handle, codec);
            handle->time_base = new AVRational { num = 1, den = settings.SampleRate };
            handle->bit_rate = settings.Bitrate;
            ffmpeg.av_channel_layout_default(&handle->ch_layout, settings.Channels);

            if (globalHeader)
            {
                handle->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            Av.Check(ffmpeg.avcodec_open2(handle, codec, null), "avcodec_open2", settings.Encoder);
            return new AudioEncoder(context, settings);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Queues samples, one span per channel, and encodes every whole frame that makes.
    /// </summary>
    /// <param name="planes">One array per channel.</param>
    /// <param name="offset">Where in each array the samples start.</param>
    /// <param name="count">How many samples per channel.</param>
    /// <param name="muxer">Where packets go.</param>
    /// <param name="stream">The muxer stream.</param>
    public void Write(float[][] planes, int offset, int count, Muxer muxer, int stream)
    {
        ArgumentNullException.ThrowIfNull(planes);
        if (planes.Length < Settings.Channels)
        {
            throw new ArgumentException($"Expected {Settings.Channels} channels, got {planes.Length}.", nameof(planes));
        }

        if (count <= 0)
        {
            return;
        }

        if (_context.Handle->sample_fmt == AVSampleFormat.AV_SAMPLE_FMT_FLTP)
        {
            WriteFloat(planes, offset, count);
        }
        else
        {
            WriteConverted(planes, offset, count);
        }

        while (ffmpeg.av_audio_fifo_size(_fifo) >= _frameSize)
        {
            SendFrame(_frameSize, muxer, stream);
        }
    }

    /// <summary>Encodes what is left in the FIFO as a short last frame and drains the encoder.</summary>
    public void Flush(Muxer muxer, int stream)
    {
        int left = ffmpeg.av_audio_fifo_size(_fifo);
        if (left > 0)
        {
            SendFrame(left, muxer, stream);
        }

        int result = ffmpeg.avcodec_send_frame(_context.Handle, null);
        if (result != Av.EndOfFile)
        {
            Av.Check(result, "avcodec_send_frame", Settings.Encoder);
        }

        Drain(muxer, stream);
        _log.Debug("{Encoder} encoded {Samples} samples", Settings.Encoder, _samplesSent);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_fifo is not null)
        {
            ffmpeg.av_audio_fifo_free(_fifo);
            _fifo = null;
        }

        _frame.Dispose();
        _packet.Dispose();
        _context.Dispose();
    }

    private void WriteFloat(float[][] planes, int offset, int count)
    {
        int channels = Settings.Channels;
        void** pointers = stackalloc void*[channels];
        var handles = new System.Runtime.InteropServices.GCHandle[channels];
        try
        {
            for (int channel = 0; channel < channels; channel++)
            {
                handles[channel] = System.Runtime.InteropServices.GCHandle.Alloc(planes[channel], System.Runtime.InteropServices.GCHandleType.Pinned);
                pointers[channel] = (float*)handles[channel].AddrOfPinnedObject() + offset;
            }

            Av.Check(ffmpeg.av_audio_fifo_write(_fifo, pointers, count), "av_audio_fifo_write");
        }
        finally
        {
            foreach (System.Runtime.InteropServices.GCHandle handle in handles)
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
        }
    }

    /// <summary>
    /// The sample format an encoder is opened with: planar float, which is what the mixer makes,
    /// when the encoder takes it; otherwise the nearest it does take, with 32 bit integers before
    /// 16 so a 24 bit PCM or FLAC file keeps its bits.
    /// </summary>
    private static AVSampleFormat ChooseFormat(AVCodecContext* context, AVCodec* codec)
    {
        void* configs = null;
        int count = 0;
        if (ffmpeg.avcodec_get_supported_config(context, codec, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_FORMAT, 0, &configs, &count) < 0 || configs == null)
        {
            return AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        }

        var supported = new ReadOnlySpan<AVSampleFormat>(configs, count);
        foreach (AVSampleFormat preferred in Preference)
        {
            if (supported.Contains(preferred))
            {
                return preferred;
            }
        }

        throw new FfmpegException($"The audio encoder takes {supported[0]}, which Jazz Hands does not write.");
    }

    private static readonly AVSampleFormat[] Preference =
    [
        AVSampleFormat.AV_SAMPLE_FMT_FLTP,
        AVSampleFormat.AV_SAMPLE_FMT_FLT,
        AVSampleFormat.AV_SAMPLE_FMT_S32,
        AVSampleFormat.AV_SAMPLE_FMT_S32P,
        AVSampleFormat.AV_SAMPLE_FMT_S16,
        AVSampleFormat.AV_SAMPLE_FMT_S16P,
    ];

    /// <summary>
    /// Converts planar float to whatever else the encoder takes and queues it: interleaved or
    /// planar, float, 32 bit integers left justified (FLAC and 24 bit PCM keep the top 24) or 16
    /// bit integers.
    /// </summary>
    private void WriteConverted(float[][] planes, int offset, int count)
    {
        int channels = Settings.Channels;
        AVSampleFormat format = _context.Handle->sample_fmt;
        bool planar = ffmpeg.av_sample_fmt_is_planar(format) != 0;
        int bytes = ffmpeg.av_get_bytes_per_sample(format);
        int needed = count * channels * bytes;
        if (_scratch.Length < needed)
        {
            _scratch = new byte[needed];
        }

        fixed (byte* scratch = _scratch)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                float[] plane = planes[channel];
                for (int sample = 0; sample < count; sample++)
                {
                    float value = Math.Clamp(plane[offset + sample], -1.0f, 1.0f);
                    int index = planar ? (channel * count) + sample : (sample * channels) + channel;
                    switch (format)
                    {
                        case AVSampleFormat.AV_SAMPLE_FMT_FLT:
                            ((float*)scratch)[index] = value;
                            break;
                        case AVSampleFormat.AV_SAMPLE_FMT_S32:
                        case AVSampleFormat.AV_SAMPLE_FMT_S32P:
                            ((int*)scratch)[index] = (int)Math.Round(value * int.MaxValue);
                            break;
                        default:
                            ((short*)scratch)[index] = (short)Math.Round(value * short.MaxValue);
                            break;
                    }
                }
            }

            void** pointers = stackalloc void*[channels];
            for (int channel = 0; channel < channels; channel++)
            {
                pointers[channel] = planar ? scratch + (channel * count * bytes) : scratch;
            }

            Av.Check(ffmpeg.av_audio_fifo_write(_fifo, pointers, count), "av_audio_fifo_write");
        }
    }

    private void SendFrame(int samples, Muxer muxer, int stream)
    {
        AVFrame* frame = _frame.Handle;
        ffmpeg.av_frame_unref(frame);
        frame->nb_samples = samples;
        frame->format = (int)_context.Handle->sample_fmt;
        frame->sample_rate = Settings.SampleRate;
        Av.Check(ffmpeg.av_channel_layout_copy(&frame->ch_layout, &_context.Handle->ch_layout), "av_channel_layout_copy");
        Av.Check(ffmpeg.av_frame_get_buffer(frame, 0), "av_frame_get_buffer");

        void** data = (void**)&frame->data;
        int read = ffmpeg.av_audio_fifo_read(_fifo, data, samples);
        Av.Check(read, "av_audio_fifo_read");

        frame->pts = _samplesSent;
        _samplesSent += samples;

        Av.Check(ffmpeg.avcodec_send_frame(_context.Handle, frame), "avcodec_send_frame", Settings.Encoder);
        Drain(muxer, stream);
    }

    private void Drain(Muxer muxer, int stream)
    {
        while (true)
        {
            int result = ffmpeg.avcodec_receive_packet(_context.Handle, _packet.Handle);
            if (result == Av.Again || result == Av.EndOfFile)
            {
                return;
            }

            Av.Check(result, "avcodec_receive_packet", Settings.Encoder);
            muxer.Write(_packet.Handle, stream, TimeBase);
        }
    }
}
