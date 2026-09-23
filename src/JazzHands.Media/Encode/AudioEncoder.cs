using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Encode;

/// <summary>What an audio encoder is asked for.</summary>
/// <param name="Encoder">The FFmpeg encoder name: aac, libopus, flac.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">Channel count: 1, 2 or 6.</param>
/// <param name="Bitrate">Bits per second, for the lossy encoders.</param>
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
            handle->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            handle->time_base = new AVRational { num = 1, den = settings.SampleRate };
            handle->bit_rate = settings.Bitrate;
            ffmpeg.av_channel_layout_default(&handle->ch_layout, settings.Channels);

            if (settings.Encoder == "flac")
            {
                handle->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S32;
            }

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
            WriteInt32(planes, offset, count);
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

    private void WriteInt32(float[][] planes, int offset, int count)
    {
        // FLAC takes interleaved 32 bit integers, left justified.
        int channels = Settings.Channels;
        int[] interleaved = new int[count * channels];
        for (int sample = 0; sample < count; sample++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                float value = Math.Clamp(planes[channel][offset + sample], -1.0f, 1.0f);
                interleaved[(sample * channels) + channel] = (int)Math.Round(value * int.MaxValue);
            }
        }

        fixed (int* data = interleaved)
        {
            void* pointer = data;
            Av.Check(ffmpeg.av_audio_fifo_write(_fifo, &pointer, count), "av_audio_fifo_write");
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
