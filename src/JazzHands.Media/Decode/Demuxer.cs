using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;
using JazzHands.Media.Probe;
using Serilog;

namespace JazzHands.Media.Decode;

/// <summary>
/// Reads packets out of one media file.
/// </summary>
/// <remarks>
/// One demuxer per open file, and thread-affine: create it, read from it and dispose it on the
/// same thread. Cross-thread use is a bug, not a race to be papered over with a lock. See the
/// ffmpeg-interop skill.
/// </remarks>
public sealed unsafe class Demuxer : IDisposable
{
    private readonly ILogger _log = Log.ForContext<Demuxer>();
    private readonly AvFormatContext _format;
    private readonly AvPacket _packet;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    /// <summary>Opens a file for reading.</summary>
    public Demuxer(string path)
        : this(path, options: null)
    {
    }

    /// <summary>Opens a file, or an image sequence pattern, with demuxer options.</summary>
    /// <param name="path">The file, or a <c>%04d</c> style pattern.</param>
    /// <param name="options">Demuxer options such as an image sequence's frame rate.</param>
    /// <param name="fileMustExist">False when the path is a pattern rather than one file.</param>
    public Demuxer(string path, IReadOnlyDictionary<string, string>? options, bool fileMustExist = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Before anything that touches FFmpeg, including the packet below: field initialisers run
        // ahead of the constructor body, so a packet allocated there would call into bindings that
        // have not been pointed at the libraries yet.
        FfmpegLoader.Initialize();

        _packet = new AvPacket();
        Path = System.IO.Path.GetFullPath(path);
        _format = AvFormatContext.OpenInput(Path, findStreamInfo: true, options, fileMustExist);
    }

    /// <summary>The file being read.</summary>
    public string Path { get; }

    /// <summary>True once the last read returned end of file.</summary>
    public bool IsAtEnd { get; private set; }

    internal AVFormatContext* FormatHandle => _format.Handle;

    /// <summary>The index of the best stream of a kind, or -1 when there is none.</summary>
    public int FindBestStream(StreamKind kind)
    {
        AVMediaType type = kind switch
        {
            StreamKind.Video => AVMediaType.AVMEDIA_TYPE_VIDEO,
            StreamKind.Audio => AVMediaType.AVMEDIA_TYPE_AUDIO,
            StreamKind.Subtitle => AVMediaType.AVMEDIA_TYPE_SUBTITLE,
            _ => AVMediaType.AVMEDIA_TYPE_UNKNOWN,
        };

        AVCodec* decoder = null;
        int index = ffmpeg.av_find_best_stream(_format.Handle, type, -1, -1, &decoder, 0);
        return index < 0 ? -1 : index;
    }

    /// <summary>The time base of a stream, for converting its packet timestamps.</summary>
    public Rational GetTimeBase(int streamIndex)
    {
        AVStream* stream = GetStream(streamIndex);
        return new Rational(stream->time_base.num, stream->time_base.den);
    }

    /// <summary>The frame rate of a video stream, as an exact rational.</summary>
    public Rational GetFrameRate(int streamIndex)
    {
        AVStream* stream = GetStream(streamIndex);
        AVRational guessed = ffmpeg.av_guess_frame_rate(_format.Handle, stream, null);
        return guessed.num > 0 && guessed.den > 0
            ? new Rational(guessed.num, guessed.den)
            : new Rational(stream->r_frame_rate.num, Math.Max(1, stream->r_frame_rate.den));
    }

    /// <summary>
    /// Reads the next packet belonging to <paramref name="streamIndex"/>, skipping others.
    /// The packet is valid until the next call; copy anything you need to keep.
    /// </summary>
    /// <returns>The packet, or null at end of file.</returns>
    internal AVPacket* ReadPacket(int streamIndex)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            _packet.Unref();
            int result = ffmpeg.av_read_frame(_format.Handle, _packet.Handle);
            if (result == Av.EndOfFile || result == ffmpeg.AVERROR_EOF)
            {
                IsAtEnd = true;
                return null;
            }

            Av.Check(result, "av_read_frame", Path);

            if (streamIndex < 0 || _packet.Handle->stream_index == streamIndex)
            {
                return _packet.Handle;
            }
        }
    }

    /// <summary>
    /// Seeks to the keyframe at or before <paramref name="target"/> on a stream. Decoding forward
    /// from there and discarding early frames is what makes a seek frame accurate; see
    /// <see cref="Seeker"/>.
    /// </summary>
    public void SeekToKeyframeBefore(int streamIndex, Flicks target)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        AVStream* stream = GetStream(streamIndex);
        long timestamp = target.ToTimebase(stream->time_base.num, stream->time_base.den, RoundingMode.Floor);

        Av.Check(
            ffmpeg.av_seek_frame(_format.Handle, streamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD),
            "av_seek_frame",
            Path);

        IsAtEnd = false;
    }

    /// <summary>
    /// Tells the demuxer to skip every stream but these, so reading one sound stream of a 4K file
    /// does not read its picture too. A container that can (MP4) does not even read their bytes.
    /// </summary>
    public void Keep(params int[] streams)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(streams);

        AVFormatContext* format = _format.Handle;
        for (int index = 0; index < format->nb_streams; index++)
        {
            format->streams[index]->discard = streams.Contains(index) ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
        }
    }

    /// <summary>Rewinds to the start of the file.</summary>
    public void Rewind()
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        Av.Check(
            ffmpeg.av_seek_frame(_format.Handle, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD),
            "av_seek_frame",
            Path);

        IsAtEnd = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _packet.Dispose();
        _format.Dispose();
    }

    internal AVStream* GetStream(int streamIndex)
    {
        AVFormatContext* context = _format.Handle;
        return streamIndex < 0 || streamIndex >= context->nb_streams
            ? throw new ArgumentOutOfRangeException(
                nameof(streamIndex),
                streamIndex,
                $"'{Path}' has {context->nb_streams} streams.")
            : context->streams[streamIndex];
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException(
                $"Demuxer for '{Path}' was created on thread {_threadId} and used on "
                + $"{Environment.CurrentManagedThreadId}. One demuxer belongs to one thread.");
        }
    }
}
