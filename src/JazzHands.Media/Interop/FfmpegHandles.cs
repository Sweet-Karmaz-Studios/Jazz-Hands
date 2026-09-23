using FFmpeg.AutoGen;

namespace JazzHands.Media.Interop;

/// <summary>
/// Owns an <c>AVFormatContext*</c> opened for input. Disposing closes the input, which also frees
/// the context; calling avformat_free_context as well is a double free.
/// </summary>
/// <remarks>
/// Every handle type here calls <see cref="FfmpegLoader.Initialize"/> before its first FFmpeg
/// call. It is a flag read once the libraries are loaded, and it removes a whole class of
/// ordering bug: a field initialiser runs before its constructor body, so a handle allocated in
/// one would otherwise call into bindings that have not been pointed at the libraries yet.
/// </remarks>
public sealed unsafe class AvFormatContext : IDisposable
{
    private AVFormatContext* _handle;

    private AvFormatContext(AVFormatContext* handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    /// <summary>The file this context was opened on.</summary>
    public string Path { get; }

    /// <summary>The raw pointer. Valid until disposal.</summary>
    public AVFormatContext* Handle => _handle is null
        ? throw new ObjectDisposedException(nameof(AvFormatContext))
        : _handle;

    /// <summary>True once disposed.</summary>
    public bool IsDisposed => _handle is null;

    /// <summary>Opens a file for demuxing and reads its stream information.</summary>
    /// <param name="path">The media file.</param>
    /// <param name="findStreamInfo">
    /// Probe the streams. Needed for anything that inspects codec parameters, which is everything
    /// except a raw packet-index scan.
    /// </param>
    /// <param name="options">
    /// Demuxer options, for example the frame rate and start number of an image sequence. Null
    /// for the defaults.
    /// </param>
    /// <param name="fileMustExist">
    /// False for an input that is not one file, such as a <c>%04d</c> image sequence pattern.
    /// </param>
    public static AvFormatContext OpenInput(
        string path,
        bool findStreamInfo = true,
        IReadOnlyDictionary<string, string>? options = null,
        bool fileMustExist = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FfmpegLoader.Initialize();
        if (fileMustExist && !File.Exists(path))
        {
            throw new FileNotFoundException($"Media file '{path}' does not exist.", path);
        }

        AVFormatContext* context = null;
        AVDictionary* dictionary = null;
        int result;
        try
        {
            if (options is not null)
            {
                foreach ((string key, string value) in options)
                {
                    Av.Check(ffmpeg.av_dict_set(&dictionary, key, value, 0), "av_dict_set", key);
                }
            }

            result = ffmpeg.avformat_open_input(&context, path, null, &dictionary);
        }
        finally
        {
            ffmpeg.av_dict_free(&dictionary);
        }

        if (result < 0)
        {
            // open_input frees the context itself when it fails.
            throw new FfmpegException(result, "avformat_open_input", path);
        }

        var wrapper = new AvFormatContext(context, path);
        try
        {
            if (findStreamInfo)
            {
                Av.Check(ffmpeg.avformat_find_stream_info(context, null), "avformat_find_stream_info", path);
            }

            return wrapper;
        }
        catch
        {
            wrapper.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle is null)
        {
            return;
        }

        fixed (AVFormatContext** handle = &_handle)
        {
            ffmpeg.avformat_close_input(handle);
        }

        _handle = null;
    }
}

/// <summary>Owns an <c>AVCodecContext*</c>.</summary>
public sealed unsafe class AvCodecContext : IDisposable
{
    private AVCodecContext* _handle;

    /// <summary>Allocates a context for a codec.</summary>
    public AvCodecContext(AVCodec* codec)
    {
        FfmpegLoader.Initialize();
        _handle = Av.CheckAlloc(ffmpeg.avcodec_alloc_context3(codec), "avcodec_alloc_context3");
    }

    /// <summary>The raw pointer. Valid until disposal.</summary>
    public AVCodecContext* Handle => _handle is null
        ? throw new ObjectDisposedException(nameof(AvCodecContext))
        : _handle;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle is null)
        {
            return;
        }

        fixed (AVCodecContext** handle = &_handle)
        {
            ffmpeg.avcodec_free_context(handle);
        }

        _handle = null;
    }
}

/// <summary>Owns an <c>AVFrame*</c>. Pooled: see FramePool, which unrefs rather than frees.</summary>
public sealed unsafe class AvFrame : IDisposable
{
    private AVFrame* _handle;

    /// <summary>Allocates an empty frame.</summary>
    public AvFrame()
    {
        FfmpegLoader.Initialize();
        _handle = Av.CheckAlloc(ffmpeg.av_frame_alloc(), "av_frame_alloc");
    }

    /// <summary>The raw pointer. Valid until disposal.</summary>
    public AVFrame* Handle => _handle is null
        ? throw new ObjectDisposedException(nameof(AvFrame))
        : _handle;

    /// <summary>Releases the frame's buffers but keeps the frame itself, ready to be filled again.</summary>
    public void Unref()
    {
        if (_handle is not null)
        {
            ffmpeg.av_frame_unref(_handle);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle is null)
        {
            return;
        }

        fixed (AVFrame** handle = &_handle)
        {
            ffmpeg.av_frame_free(handle);
        }

        _handle = null;
    }
}

/// <summary>Owns an <c>AVPacket*</c>.</summary>
public sealed unsafe class AvPacket : IDisposable
{
    private AVPacket* _handle;

    /// <summary>Allocates an empty packet.</summary>
    public AvPacket()
    {
        FfmpegLoader.Initialize();
        _handle = Av.CheckAlloc(ffmpeg.av_packet_alloc(), "av_packet_alloc");
    }

    /// <summary>The raw pointer. Valid until disposal.</summary>
    public AVPacket* Handle => _handle is null
        ? throw new ObjectDisposedException(nameof(AvPacket))
        : _handle;

    /// <summary>Releases the packet's payload, ready to be filled again.</summary>
    public void Unref()
    {
        if (_handle is not null)
        {
            ffmpeg.av_packet_unref(_handle);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle is null)
        {
            return;
        }

        fixed (AVPacket** handle = &_handle)
        {
            ffmpeg.av_packet_free(handle);
        }

        _handle = null;
    }
}

/// <summary>Owns an <c>AVBufferRef*</c>, which is how FFmpeg hands out hardware device contexts.</summary>
public sealed unsafe class AvBufferRef : IDisposable
{
    private AVBufferRef* _handle;

    /// <summary>Takes ownership of an existing reference.</summary>
    public AvBufferRef(AVBufferRef* handle) => _handle = handle;

    /// <summary>The raw pointer. Valid until disposal.</summary>
    public AVBufferRef* Handle => _handle is null
        ? throw new ObjectDisposedException(nameof(AvBufferRef))
        : _handle;

    /// <summary>True once disposed.</summary>
    public bool IsDisposed => _handle is null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle is null)
        {
            return;
        }

        fixed (AVBufferRef** handle = &_handle)
        {
            ffmpeg.av_buffer_unref(handle);
        }

        _handle = null;
    }
}
