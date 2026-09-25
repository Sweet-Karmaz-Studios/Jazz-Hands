using JazzHands.Core.Time;
using JazzHands.Media.Decode;
using JazzHands.Media.Interop;

namespace JazzHands.Media.Audio;

/// <summary>
/// Reads a stretch of an audio stream as one channel of samples, for analysis: beats, onsets.
/// </summary>
public static class MonoReader
{
    /// <summary>
    /// The samples of one audio stream from a source time for a length, mixed to mono at a rate.
    /// Sample zero is <paramref name="from"/>; a stream that starts later is silent until it does.
    /// </summary>
    public static float[] Read(string path, int streamIndex, Flicks from, Flicks length, int rate = 48000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FfmpegLoader.Initialize();

        long count = Math.Max(0, length.ToSamples(rate, RoundingMode.Nearest));
        float[] samples = new float[count];
        using var demuxer = new Demuxer(path);
        using var decoder = new AudioDecoder(demuxer, streamIndex, rate, 1);
        if (from > Flicks.Zero)
        {
            decoder.SeekTo(from);
        }

        long start = from.ToSamples(rate, RoundingMode.Nearest);
        long position = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using AudioFrame? frame = decoder.ReadFrame();
            if (frame is null)
            {
                break;
            }

            // The first block says where the stream is; after that, count, as the waveform does.
            if (position < 0)
            {
                position = frame.Pts.ToSamples(rate, RoundingMode.Nearest);
            }

            ReadOnlySpan<float> plane = frame.Plane(0);
            for (int index = 0; index < frame.Frames; index++, position++)
            {
                long at = position - start;
                if (at >= count)
                {
                    return samples;
                }

                if (at >= 0)
                {
                    samples[at] = plane[index];
                }
            }
        }

        return samples;
    }
}
