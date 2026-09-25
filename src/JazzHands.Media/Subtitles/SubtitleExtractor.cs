using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using JazzHands.Core.Model;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Time;
using JazzHands.Core.Titles;
using JazzHands.Media.Decode;
using JazzHands.Media.Interop;

namespace JazzHands.Media.Subtitles;

/// <summary>
/// Reads a text subtitle stream out of a media file into cues: SubRip, ASS, WebVTT or mov_text
/// in a Matroska or MP4 file.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg's text subtitle decoders all hand back ASS dialogue lines, whatever the stream was, so
/// every cue goes through the same ASS reader (<see cref="AssFormat.FromAss"/>): bold, italic,
/// colour and alignment become markup and place, and tags it cannot draw are kept. An ASS
/// stream's own header (its styles) is in the stream's extra data, and gives the file's style.
/// </para>
/// <para>
/// A cue's time is its packet's, and its length the packet's duration, or the decoder's display
/// time when the packet has none. Picture subtitles (PGS, DVB, VobSub) are pictures, not text:
/// they cannot be edited and are refused.
/// </para>
/// </remarks>
public static unsafe class SubtitleExtractor
{
    /// <summary>Reads one subtitle stream of a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="streamIndex">The stream's index in the file, as its probe numbered it.</param>
    /// <exception cref="FfmpegException">The file cannot be read, the stream is not subtitles or has no decoder.</exception>
    /// <exception cref="SubtitleFormatException">The stream is picture subtitles.</exception>
    public static SubtitleDocument Extract(string path, int streamIndex)
    {
        using var demuxer = new Demuxer(path);
        AVStream* stream = demuxer.GetStream(streamIndex);
        AVCodecParameters* parameters = stream->codecpar;
        string codecName = ffmpeg.avcodec_get_name(parameters->codec_id) ?? "unknown";

        if (parameters->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE)
        {
            throw new SubtitleFormatException($"Stream {streamIndex} of '{Path.GetFileName(path)}' is {parameters->codec_type.ToString().Replace("AVMEDIA_TYPE_", string.Empty, StringComparison.Ordinal).ToLowerInvariant()}, not subtitles.");
        }

        AVCodecDescriptor* descriptor = ffmpeg.avcodec_descriptor_get(parameters->codec_id);
        if (descriptor is not null && (descriptor->props & ffmpeg.AV_CODEC_PROP_BITMAP_SUB) != 0)
        {
            throw new SubtitleFormatException($"Stream {streamIndex} of '{Path.GetFileName(path)}' is {codecName}, subtitles drawn as pictures, which cannot be edited as text.");
        }

        AVCodec* codec = ffmpeg.avcodec_find_decoder(parameters->codec_id);
        if (codec is null)
        {
            throw new FfmpegException(Av.DecoderNotFound, "avcodec_find_decoder", $"{codecName} in '{path}'");
        }

        using var context = new AvCodecContext(codec);
        Av.Check(ffmpeg.avcodec_parameters_to_context(context.Handle, parameters), "avcodec_parameters_to_context", path);
        context.Handle->pkt_timebase = stream->time_base;
        Av.Check(ffmpeg.avcodec_open2(context.Handle, codec, null), "avcodec_open2", path);

        var cues = new List<SubtitleCue>();
        var warnings = new List<string>();
        AVRational timeBase = stream->time_base;

        while (demuxer.ReadPacket(streamIndex) is var packet && packet is not null)
        {
            AVSubtitle subtitle;
            int got = 0;
            if (ffmpeg.avcodec_decode_subtitle2(context.Handle, &subtitle, &got, packet) < 0 || got == 0)
            {
                continue;
            }

            try
            {
                long pts = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
                if (pts == ffmpeg.AV_NOPTS_VALUE)
                {
                    Warn(warnings, "A subtitle without a time was skipped.");
                    continue;
                }

                Flicks start = Flicks.FromTimebase(pts, timeBase.num, timeBase.den) + Flicks.FromMilliseconds(subtitle.start_display_time);
                Flicks end = packet->duration > 0
                    ? Flicks.FromTimebase(pts + packet->duration, timeBase.num, timeBase.den)
                    : Flicks.FromTimebase(pts, timeBase.num, timeBase.den) + Flicks.FromMilliseconds(subtitle.end_display_time);

                for (uint index = 0; index < subtitle.num_rects; index++)
                {
                    AVSubtitleRect* rect = subtitle.rects[index];
                    if (Cue(rect, start, end, warnings) is { } cue)
                    {
                        cues.Add(cue);
                    }
                }
            }
            finally
            {
                ffmpeg.avsubtitle_free(&subtitle);
            }
        }

        // An ASS stream carries its header, styles and all, in its extra data.
        SubtitleStyle? style = null;
        string? header = null;
        if (parameters->codec_id is AVCodecID.AV_CODEC_ID_ASS or AVCodecID.AV_CODEC_ID_SSA && parameters->extradata_size > 0)
        {
            string text = Marshal.PtrToStringUTF8((IntPtr)parameters->extradata, parameters->extradata_size);
            SubtitleDocument declared = AssFormat.Parse(text);
            style = declared.Style;
            header = declared.Header;
        }

        return new SubtitleDocument([.. cues.OrderBy(cue => cue.Start)], style, [.. warnings], header);
    }

    /// <summary>
    /// One decoded rectangle as a cue. The decoders write ASS dialogue as
    /// <c>ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text</c>.
    /// </summary>
    private static SubtitleCue? Cue(AVSubtitleRect* rect, Flicks start, Flicks end, List<string> warnings)
    {
        if (rect->ass is not null)
        {
            string line = Marshal.PtrToStringUTF8((IntPtr)rect->ass) ?? string.Empty;
            string[] fields = line.Split(',', 9);
            string body = fields.Length == 9 ? fields[8] : line;
            string? name = fields.Length == 9 && fields[3].Trim().Length > 0 ? fields[3].Trim() : null;
            (string markup, SubtitleAlign? align, List<string> dropped) = AssFormat.FromAss(body);
            foreach (string tag in dropped)
            {
                Warn(warnings, $"The ASS tag \\{tag} is kept for writing ASS back but not drawn.");
            }

            return new SubtitleCue(start, end < start ? start : end, markup.TrimEnd('\n'), align ?? SubtitleAlign.Bottom, Name: name, Raw: dropped.Count > 0 ? body : null);
        }

        if (rect->text is not null)
        {
            string markup = TitleMarkup.Escape(Marshal.PtrToStringUTF8((IntPtr)rect->text) ?? string.Empty);
            return new SubtitleCue(start, end < start ? start : end, markup);
        }

        Warn(warnings, "A picture inside a text subtitle stream was skipped.");
        return null;
    }

    private static void Warn(List<string> warnings, string warning)
    {
        if (!warnings.Contains(warning))
        {
            warnings.Add(warning);
        }
    }
}
