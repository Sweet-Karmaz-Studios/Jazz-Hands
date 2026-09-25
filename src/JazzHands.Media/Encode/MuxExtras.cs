using System.Globalization;
using System.Text;
using FFmpeg.AutoGen;
using JazzHands.Core.Model;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Time;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Encode;

/// <summary>A subtitle stream to put in a file.</summary>
/// <param name="Codec">The FFmpeg encoder: mov_text for MP4 and MOV, subrip or ass for Matroska.</param>
/// <param name="Document">The cues, at their times in the output, and the style an ASS stream carries.</param>
/// <param name="Language">An ISO 639-2 code, or null.</param>
/// <param name="Title">What players call the stream.</param>
/// <param name="Default">Shown unless the viewer turns it off.</param>
public sealed record MuxSubtitleTrack(string Codec, SubtitleDocument Document, string? Language, string? Title, bool Default);

/// <summary>A chapter to put in a file.</summary>
/// <param name="Start">Where it starts in the output.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Title">Its title.</param>
public sealed record MuxChapter(Flicks Start, Flicks End, string Title);

/// <summary>
/// What a file carries besides its picture and sound: subtitle streams and chapters.
/// </summary>
/// <remarks>
/// <para>
/// Subtitles are encoded by FFmpeg's own subtitle encoders, which all take ASS event lines: a
/// cue's markup becomes one (<see cref="AssFormat.Line"/>) and the encoder makes of it what its
/// format carries, styled boxes for mov_text, tags for SubRip, the line itself for ASS. A cue is
/// written when the file's other streams reach its start, so the muxer interleaves it where it
/// belongs rather than buffering the whole picture behind it.
/// </para>
/// <para>
/// Chapters go into the container's chapter list before its header: Matroska keeps them
/// natively, MP4 as a QuickTime chapter track and a Nero <c>chpl</c> box, which is what YouTube
/// and most players read.
/// </para>
/// </remarks>
public sealed unsafe class MuxExtras : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MuxExtras>();
    private readonly List<Lane> _lanes = [];
    private bool _disposed;

    /// <summary>Holds what to add; nothing is opened until <see cref="Open"/>.</summary>
    public MuxExtras(IEnumerable<MuxSubtitleTrack> subtitles, IEnumerable<MuxChapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(subtitles);
        ArgumentNullException.ThrowIfNull(chapters);
        Subtitles = [.. subtitles];
        Chapters = [.. chapters];
    }

    /// <summary>The subtitle streams.</summary>
    public IReadOnlyList<MuxSubtitleTrack> Subtitles { get; }

    /// <summary>The chapters.</summary>
    public IReadOnlyList<MuxChapter> Chapters { get; }

    /// <summary>True when there is nothing to add.</summary>
    public bool IsEmpty => Subtitles.Count == 0 && Chapters.Count == 0;

    /// <summary>Adds the streams and chapters to a muxer, before its header is written.</summary>
    public void Open(Muxer muxer)
    {
        ArgumentNullException.ThrowIfNull(muxer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (MuxSubtitleTrack track in Subtitles)
        {
            var encoder = new Encoder(track.Codec, AssFormat.EncoderHeader(track.Document.Style), muxer.Path);
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (track.Title is { Length: > 0 } title)
            {
                metadata["title"] = title;
            }

            if (track.Language is { Length: > 0 } language)
            {
                metadata["language"] = language;
            }

            int stream = muxer.AddEncodedStream(encoder.Context.Handle, metadata);
            if (track.Default)
            {
                muxer.SetDefault(stream);
            }

            _lanes.Add(new Lane(track, encoder, stream));
        }

        muxer.AddChapters(Chapters);
    }

    /// <summary>Writes every cue that starts before a time in the output.</summary>
    public void WriteUpTo(Muxer muxer, Flicks time)
    {
        ArgumentNullException.ThrowIfNull(muxer);
        foreach (Lane lane in _lanes)
        {
            while (lane.Next < lane.Track.Document.Cues.Length && lane.Track.Document.Cues[lane.Next].Start < time)
            {
                lane.Write(muxer, lane.Track.Document.Cues[lane.Next]);
                lane.Next++;
            }
        }
    }

    /// <summary>Writes the cues that are left, before the muxer's trailer.</summary>
    public void Close(Muxer muxer) => WriteUpTo(muxer, Flicks.MaxValue);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Lane lane in _lanes)
        {
            lane.Encoder.Dispose();
        }
    }

    private sealed class Lane(MuxSubtitleTrack track, Encoder encoder, int stream)
    {
        public MuxSubtitleTrack Track { get; } = track;

        public Encoder Encoder { get; } = encoder;

        public int Next { get; set; }

        public void Write(Muxer muxer, SubtitleCue cue)
        {
            long start = cue.Start.ToTimebase(1, 1000, RoundingMode.Nearest);
            long end = Math.Max(start + 1, cue.End.ToTimebase(1, 1000, RoundingMode.Nearest));
            string line = string.Create(CultureInfo.InvariantCulture, $"{Next},0,Default,{cue.Name?.Replace(',', ' ') ?? string.Empty},0,0,0,,{AssFormat.Line(cue)}");

            using AvPacket packet = Encoder.Encode(line, start, end);
            if (packet.Handle->size > 0)
            {
                muxer.Write(packet.Handle, stream, new Rational(1, 1000));
            }
        }
    }

    /// <summary>One FFmpeg subtitle encoder, fed ASS events, in milliseconds.</summary>
    private sealed class Encoder : IDisposable
    {
        private readonly byte[] _buffer = new byte[256 * 1024];

        public Encoder(string codecName, string header, string path)
        {
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
            if (codec is null)
            {
                throw new FfmpegException($"The subtitle encoder '{codecName}' is not in this FFmpeg build.");
            }

            Context = new AvCodecContext(codec);
            AVCodecContext* context = Context.Handle;
            context->time_base = new AVRational { num = 1, den = 1000 };

            // The encoder owns and frees its header, so it has to come from FFmpeg's allocator.
            byte[] bytes = Encoding.UTF8.GetBytes(header);
            byte* copy = (byte*)ffmpeg.av_mallocz((ulong)bytes.Length + 1);
            bytes.CopyTo(new Span<byte>(copy, bytes.Length));
            context->subtitle_header = copy;
            context->subtitle_header_size = bytes.Length;

            Av.Check(ffmpeg.avcodec_open2(context, codec, null), "avcodec_open2", $"{codecName} for {path}");
            Log.Debug("Opened the {Codec} subtitle encoder for {Path}", codecName, path);
        }

        public AvCodecContext Context { get; }

        public AvPacket Encode(string line, long startMs, long endMs)
        {
            AVSubtitle subtitle = default;
            subtitle.format = 1;
            subtitle.start_display_time = 0;
            subtitle.end_display_time = (uint)(endMs - startMs);
            subtitle.pts = startMs * 1000;
            subtitle.num_rects = 1;
            subtitle.rects = (AVSubtitleRect**)ffmpeg.av_mallocz((ulong)sizeof(AVSubtitleRect*));
            AVSubtitleRect* rect = (AVSubtitleRect*)ffmpeg.av_mallocz((ulong)sizeof(AVSubtitleRect));
            rect->type = AVSubtitleType.SUBTITLE_ASS;
            rect->ass = (byte*)ffmpeg.av_strdup(line);
            subtitle.rects[0] = rect;

            int size;
            try
            {
                fixed (byte* buffer = _buffer)
                {
                    size = ffmpeg.avcodec_encode_subtitle(Context.Handle, buffer, _buffer.Length, &subtitle);
                }
            }
            finally
            {
                ffmpeg.avsubtitle_free(&subtitle);
            }

            Av.Check(size, "avcodec_encode_subtitle", line);

            var packet = new AvPacket();
            Av.Check(ffmpeg.av_new_packet(packet.Handle, size), "av_new_packet", "subtitle");
            _buffer.AsSpan(0, size).CopyTo(new Span<byte>(packet.Handle->data, size));
            packet.Handle->pts = startMs;
            packet.Handle->dts = startMs;
            packet.Handle->duration = endMs - startMs;
            return packet;
        }

        public void Dispose() => Context.Dispose();
    }
}
