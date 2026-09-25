using FFmpeg.AutoGen;
using JazzHands.Core.Time;
using JazzHands.Media.Decode;
using JazzHands.Media.Interop;

namespace JazzHands.Media.SmartCut;

/// <summary>
/// Checks a smart cut's file after it is written: the picture is the source's codec with the
/// source's parameter sets, every frame is there exactly once and in its place, and each sound
/// stream is there.
/// </summary>
/// <remarks>
/// The packet check reads only packet headers, so it costs a fraction of the cut and runs after
/// every one. Decoding checks the frames themselves, as a player would, and is for tests and
/// <c>jazz export --verify</c>.
/// </remarks>
public static unsafe class SmartCutVerifier
{
    /// <summary>What is wrong with a smart cut's file, or nothing.</summary>
    /// <param name="path">The file.</param>
    /// <param name="source">The source's description.</param>
    /// <param name="frames">The frames it should hold.</param>
    /// <param name="audioStreams">The sound streams it should hold.</param>
    /// <param name="decode">Decode every frame too.</param>
    /// <param name="cancellationToken">Stops a decode.</param>
    public static IReadOnlyList<string> Check(string path, MatchSource source, long frames, int audioStreams, bool decode = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var problems = new List<string>();

        using (var demuxer = new Demuxer(path))
        {
            int video = demuxer.FindBestStream(Probe.StreamKind.Video);
            if (video < 0)
            {
                return ["The file has no picture."];
            }

            AVCodecParameters* parameters = demuxer.GetStream(video)->codecpar;
            string codec = ffmpeg.avcodec_get_name(parameters->codec_id) ?? "unknown";
            if (codec != source.Codec)
            {
                problems.Add($"The picture is {codec}, not the source's {source.Codec}.");
            }
            else if (ParameterSets.Read(codec, new ReadOnlySpan<byte>(parameters->extradata, parameters->extradata_size)).Signature is not { } signature)
            {
                problems.Add("The picture's parameter sets could not be read.");
            }
            else if (signature.Differences(source.Signature) is { } different)
            {
                problems.Add($"The picture's parameter sets are not the source's: {different}.");
            }

            int sound = 0;
            for (int index = 0; index < demuxer.FormatHandle->nb_streams; index++)
            {
                if (demuxer.GetStream(index)->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    sound++;
                }
            }

            if (sound != audioStreams)
            {
                problems.Add($"The file has {sound} sound stream(s) where {audioStreams} were due.");
            }

            demuxer.Keep(video);
            Rational timeBase = demuxer.GetTimeBase(video);
            var seen = new List<long>();
            AVPacket* packet;
            while ((packet = demuxer.ReadPacket(video)) is not null)
            {
                if (packet->pts == ffmpeg.AV_NOPTS_VALUE)
                {
                    problems.Add("A picture packet has no presentation time.");
                    break;
                }

                seen.Add(Flicks.FromTimebase(packet->pts, timeBase).ToFrames(source.FrameRate, RoundingMode.Nearest));
            }

            Frames(seen, frames, "packet", problems);
        }

        if (decode && problems.Count == 0)
        {
            var shown = new List<long>();
            using Seeker seeker = Seeker.Open(path);
            while (seeker.ReadNext() is { } frame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (frame)
                {
                    shown.Add(frame.Pts.ToFrames(source.FrameRate, RoundingMode.Nearest));
                }
            }

            Frames(shown, frames, "decoded frame", problems);
        }

        return problems;
    }

    /// <summary>Frames 0 to the count, each once, where they were due.</summary>
    private static void Frames(List<long> seen, long expected, string what, List<string> problems)
    {
        seen.Sort();
        if (seen.Count != expected)
        {
            problems.Add($"The picture has {seen.Count} {what}s where {expected} were due.");
        }

        long first = seen.Count > 0 ? seen[0] : 0;
        for (int index = 0; index < seen.Count; index++)
        {
            if (seen[index] != first + index)
            {
                bool repeated = index > 0 && seen[index] == seen[index - 1];
                problems.Add(repeated
                    ? $"Frame {seen[index]} has two {what}s."
                    : $"There is no {what} for frame {first + index}.");
                return;
            }
        }
    }
}
