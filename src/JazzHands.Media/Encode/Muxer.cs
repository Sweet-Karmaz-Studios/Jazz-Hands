using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Encode;

/// <summary>
/// Writes packets into a container file: MP4, MOV, Matroska or WebM.
/// </summary>
/// <remarks>
/// Streams are added first, either copied from a source's codec parameters or taken from an
/// encoder, then the header is written, then packets, then the trailer. Packets arrive with a
/// time base of their own and are rescaled to the stream's, which the muxer may have changed
/// when the header was written (MP4 raises a video time base until it has enough resolution).
///
/// Output is bit exact by default: no library version strings and no random Matroska segment
/// identifier, so the same export twice gives the same bytes. That is what lets the CLI and the
/// GUI be compared by hash, and it costs nothing a player cares about.
///
/// Thread affine, like everything that holds a format context. One thread writes.
/// </remarks>
public sealed unsafe class Muxer : IDisposable
{
    private readonly ILogger _log = Log.ForContext<Muxer>();
    private readonly List<StreamState> _streams = [];
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private AVFormatContext* _context;
    private bool _headerWritten;
    private bool _finished;

    private Muxer(AVFormatContext* context, string path, string formatName)
    {
        _context = context;
        Path = path;
        FormatName = formatName;
    }

    /// <summary>The file being written.</summary>
    public string Path { get; }

    /// <summary>The muxer's short name, for example mp4 or matroska.</summary>
    public string FormatName { get; }

    /// <summary>How many streams have been added.</summary>
    public int StreamCount => _streams.Count;

    /// <summary>Bytes written so far, for progress.</summary>
    public long BytesWritten => _context is null || _context->pb is null ? 0 : ffmpeg.avio_tell(_context->pb);

    /// <summary>True when the container keeps a global header, so encoders must be opened with one.</summary>
    public bool NeedsGlobalHeader => (_context->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;

    /// <summary>The FFmpeg muxer name for a file extension.</summary>
    /// <returns>The name, or null for an extension Jazz Hands does not write.</returns>
    public static string? FormatForExtension(string extension) =>
        extension.TrimStart('.').ToLowerInvariant() switch
        {
            "mp4" or "m4v" => "mp4",
            "mov" => "mov",
            "mkv" => "matroska",
            "webm" => "webm",
            _ => null,
        };

    /// <summary>Creates the output file's context. Nothing is written until <see cref="WriteHeader"/>.</summary>
    /// <param name="path">Where to write.</param>
    /// <param name="formatName">The muxer, or null to choose from the extension.</param>
    /// <param name="bitExact">Leave out version strings and random identifiers.</param>
    public static Muxer Create(string path, string? formatName = null, bool bitExact = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FfmpegLoader.Initialize();

        string full = System.IO.Path.GetFullPath(path);
        string format = formatName
            ?? FormatForExtension(System.IO.Path.GetExtension(full))
            ?? throw new ArgumentException(
                $"'{System.IO.Path.GetExtension(full)}' is not a container Jazz Hands writes. Use .mp4, .mov, .mkv or .webm.",
                nameof(path));

        AVFormatContext* context = null;
        Av.Check(ffmpeg.avformat_alloc_output_context2(&context, null, format, full), "avformat_alloc_output_context2", full);

        if (bitExact)
        {
            context->flags |= ffmpeg.AVFMT_FLAG_BITEXACT;
        }

        return new Muxer(context, full, format);
    }

    /// <summary>Adds a stream whose packets are copied from a source, keeping its codec parameters.</summary>
    /// <param name="parameters">The source stream's codec parameters.</param>
    /// <param name="timeBase">The time base packets will be given in.</param>
    /// <param name="metadata">Stream tags to carry over, such as an OBS track title.</param>
    /// <returns>The output stream index.</returns>
    internal int AddCopiedStream(AVCodecParameters* parameters, Rational timeBase, IReadOnlyDictionary<string, string>? metadata = null)
    {
        AVStream* stream = NewStream();
        Av.Check(ffmpeg.avcodec_parameters_copy(stream->codecpar, parameters), "avcodec_parameters_copy", Path);

        // A tag chosen for the source's container can be wrong for this one: an MKV's HEVC
        // stream carries no tag, and an MP4 wants hvc1 where the source said hev1. Zero lets the
        // muxer pick.
        stream->codecpar->codec_tag = 0;
        stream->time_base = new AVRational { num = (int)timeBase.Num, den = (int)timeBase.Den };

        if (parameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && FormatName is "mp4" or "mov"
            && parameters->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
        {
            // hvc1 is what Apple players and Films & TV insist on for HEVC in MP4.
            stream->codecpar->codec_tag = MakeTag('h', 'v', 'c', '1');
        }

        SetMetadata(stream, metadata);
        return Register(stream, timeBase);
    }

    /// <summary>Adds a stream fed by an encoder, taking its parameters from the open codec context.</summary>
    /// <returns>The output stream index.</returns>
    internal int AddEncodedStream(AVCodecContext* encoder, IReadOnlyDictionary<string, string>? metadata = null)
    {
        AVStream* stream = NewStream();
        Av.Check(ffmpeg.avcodec_parameters_from_context(stream->codecpar, encoder), "avcodec_parameters_from_context", Path);
        stream->time_base = encoder->time_base;

        if (encoder->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
        {
            stream->avg_frame_rate = encoder->framerate;
            stream->r_frame_rate = encoder->framerate;

            if (FormatName is "mp4" or "mov" && encoder->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
            {
                stream->codecpar->codec_tag = MakeTag('h', 'v', 'c', '1');
            }
        }

        SetMetadata(stream, metadata);
        return Register(stream, new Rational(encoder->time_base.num, encoder->time_base.den));
    }

    /// <summary>Opens the file and writes the container header.</summary>
    /// <param name="fastStart">For MP4 and MOV, move the index to the front so a player can start before the whole file arrives.</param>
    public void WriteHeader(bool fastStart = false)
    {
        VerifyThread();
        if (_headerWritten)
        {
            throw new InvalidOperationException("The header has already been written.");
        }

        if (_streams.Count == 0)
        {
            throw new InvalidOperationException("A file needs at least one stream.");
        }

        if ((_context->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            AVIOContext* io = null;
            Av.Check(ffmpeg.avio_open(&io, Path, ffmpeg.AVIO_FLAG_WRITE), "avio_open", Path);
            _context->pb = io;
        }

        AVDictionary* options = null;
        try
        {
            if (fastStart && FormatName is "mp4" or "mov")
            {
                ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
            }

            Av.Check(ffmpeg.avformat_write_header(_context, &options), "avformat_write_header", Path);
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }

        // The muxer may have chosen a finer time base than the one asked for.
        for (int index = 0; index < _streams.Count; index++)
        {
            AVRational chosen = _context->streams[index]->time_base;
            _streams[index] = _streams[index] with { Muxed = new Rational(chosen.num, chosen.den) };
        }

        _headerWritten = true;
        _log.Debug("Writing {Path} as {Format} with {Streams} streams", Path, FormatName, _streams.Count);
    }

    /// <summary>The time base the muxer settled on for a stream, after the header.</summary>
    public Rational TimeBaseOf(int stream) => _streams[stream].Muxed;

    /// <summary>
    /// Writes one packet, rescaling its timestamps from <paramref name="timeBase"/> to the stream's.
    /// The packet's payload is taken: it is empty when this returns.
    /// </summary>
    internal void Write(AVPacket* packet, int stream, Rational timeBase)
    {
        VerifyThread();
        if (!_headerWritten || _finished)
        {
            throw new InvalidOperationException("Packets go between the header and the trailer.");
        }

        Rational target = _streams[stream].Muxed;
        packet->stream_index = stream;
        ffmpeg.av_packet_rescale_ts(
            packet,
            new AVRational { num = (int)timeBase.Num, den = (int)timeBase.Den },
            new AVRational { num = (int)target.Num, den = (int)target.Den });
        packet->pos = -1;

        Av.Check(ffmpeg.av_interleaved_write_frame(_context, packet), "av_interleaved_write_frame", Path);
    }

    /// <summary>Flushes what is buffered and writes the trailer. The file is complete after this.</summary>
    public void Finish()
    {
        VerifyThread();
        if (_finished)
        {
            return;
        }

        if (!_headerWritten)
        {
            throw new InvalidOperationException("Nothing was written.");
        }

        Av.Check(ffmpeg.av_interleaved_write_frame(_context, null), "av_interleaved_write_frame", Path);
        Av.Check(ffmpeg.av_write_trailer(_context), "av_write_trailer", Path);
        _finished = true;
        CloseFile();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_context is null)
        {
            return;
        }

        CloseFile();
        ffmpeg.avformat_free_context(_context);
        _context = null;
    }

    private AVStream* NewStream()
    {
        VerifyThread();
        if (_headerWritten)
        {
            throw new InvalidOperationException("Streams are added before the header is written.");
        }

        return Av.CheckAlloc(ffmpeg.avformat_new_stream(_context, null), "avformat_new_stream");
    }

    private int Register(AVStream* stream, Rational timeBase)
    {
        _streams.Add(new StreamState(timeBase, timeBase));
        return stream->index;
    }

    private void CloseFile()
    {
        if (_context->pb is not null && (_context->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            ffmpeg.avio_closep(&_context->pb);
        }
    }

    /// <summary>
    /// Carries a stream's name and language, and nothing else a source container said about it.
    /// </summary>
    /// <remarks>
    /// Matroska tags every stream with its DURATION and the encoder that made it; copied, the
    /// first would describe the source rather than the stretch that was cut from it. MP4 reads a
    /// title back from its handler name, which is also what VLC and MPV show as the track name,
    /// so a title is written there too.
    /// </remarks>
    private void SetMetadata(AVStream* stream, IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return;
        }

        string? title = metadata.GetValueOrDefault("title");
        string? language = metadata.GetValueOrDefault("language");

        if (title is { Length: > 0 })
        {
            ffmpeg.av_dict_set(&stream->metadata, "title", title, 0);

            if (FormatName is "mp4" or "mov")
            {
                ffmpeg.av_dict_set(&stream->metadata, "handler_name", title, 0);
            }
        }

        if (language is { Length: > 0 } && !string.Equals(language, "und", StringComparison.OrdinalIgnoreCase))
        {
            ffmpeg.av_dict_set(&stream->metadata, "language", language, 0);
        }
    }

    private static uint MakeTag(char a, char b, char c, char d) =>
        a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException(
                $"The muxer for '{Path}' was created on thread {_threadId} and used on {Environment.CurrentManagedThreadId}.");
        }
    }

    private readonly record struct StreamState(Rational Requested, Rational Muxed);
}
