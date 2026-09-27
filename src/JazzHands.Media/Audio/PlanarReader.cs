using JazzHands.Core.Time;
using JazzHands.Media.Decode;
using JazzHands.Media.Interop;

namespace JazzHands.Media.Audio;

/// <summary>
/// Reads a whole audio stream as planar samples, every channel of it, at a rate: for processing
/// done ahead over a recording (speech enhancement, Phase 43).
/// </summary>
public static class PlanarReader
{
    /// <summary>
    /// Every channel of a stream from its start for a length, at a rate. Sample zero is the stream's
    /// time zero; a stream that starts later is silent until it does.
    /// </summary>
    public static float[][] Read(string path, int streamIndex, int channels, Flicks length, int rate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        FfmpegLoader.Initialize();

        long count = Math.Max(0, length.ToSamples(rate, RoundingMode.Nearest));
        float[][] planes = [.. Enumerable.Range(0, channels).Select(_ => new float[count])];
        using var demuxer = new Demuxer(path);
        using var decoder = new AudioDecoder(demuxer, streamIndex, rate, channels);
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

            for (int index = 0; index < frame.Frames; index++, position++)
            {
                if (position >= count)
                {
                    return planes;
                }

                if (position >= 0)
                {
                    for (int channel = 0; channel < channels; channel++)
                    {
                        planes[channel][position] = frame.Plane(channel)[index];
                    }
                }
            }
        }

        return planes;
    }
}
