using System.Diagnostics;
using JazzHands.Core.Time;
using JazzHands.Media.Audio;
using JazzHands.Media.Decode;

namespace JazzHands.Media.Waveforms;

/// <summary>
/// Reads a whole audio stream once and keeps its peaks, a millisecond at a time.
/// </summary>
/// <remarks>
/// Decoding is the cost and it is small: AAC decodes at several hundred times real time, so ten
/// minutes of stereo is a second or two, most of it the demuxer reading past the picture. The
/// stream is read from the start in order, and snapshots are handed out on the way so that a
/// long file fills in from the left rather than appearing all at once.
/// </remarks>
public static class PeakExtractor
{
    /// <summary>Reads a stream's peaks.</summary>
    /// <param name="path">The file.</param>
    /// <param name="streamIndex">The audio stream.</param>
    /// <param name="duration">How long the stream is, to size the arrays; a guess is fine.</param>
    /// <param name="progress">Called with a snapshot every <paramref name="progressEvery"/>, or null.</param>
    /// <param name="progressEvery">How often to take snapshots.</param>
    /// <param name="cancellationToken">Stops the read; what was read so far is thrown away.</param>
    /// <returns>The complete peaks.</returns>
    public static AudioPeaks Extract(
        string path,
        int streamIndex,
        Flicks duration,
        Action<AudioPeaks>? progress = null,
        TimeSpan progressEvery = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FfmpegLoader.Initialize();

        using var demuxer = new Demuxer(path);
        using var decoder = new AudioDecoder(demuxer, streamIndex);

        int capacity = (int)Math.Clamp(AudioPeaks.WindowAt(duration) + 64, 1024, int.MaxValue / 2);
        sbyte[] minimum = new sbyte[capacity];
        sbyte[] maximum = new sbyte[capacity];

        int rate = decoder.SampleRate;
        long sample = -1;
        int filled = 0;
        int window = -1;
        int low = sbyte.MaxValue;
        int high = sbyte.MinValue;

        long every = progressEvery > TimeSpan.Zero ? (long)(progressEvery.TotalSeconds * Stopwatch.Frequency) : long.MaxValue;
        long nextProgress = Stopwatch.GetTimestamp() + every;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using AudioFrame? frame = decoder.ReadFrame();
            if (frame is null)
            {
                break;
            }

            // The first block says where the stream starts; after that, count samples, because
            // container timestamps can be coarser than a sample (Matroska's are milliseconds).
            if (sample < 0)
            {
                sample = Math.Max(0, frame.Pts.ToSamples(rate, RoundingMode.Nearest));
            }

            int channels = frame.Channels;
            int count = frame.Frames;

            for (int index = 0; index < count; index++, sample++)
            {
                int at = (int)(sample * AudioPeaks.WindowsPerSecond / rate);
                if (at != window)
                {
                    if (window >= 0)
                    {
                        Store(ref minimum, ref maximum, window, low, high);
                        filled = window + 1;
                    }

                    window = at;
                    low = sbyte.MaxValue;
                    high = sbyte.MinValue;
                }

                for (int channel = 0; channel < channels; channel++)
                {
                    sbyte value = AudioPeaks.Quantise(frame.Plane(channel)[index]);
                    if (value < low)
                    {
                        low = value;
                    }

                    if (value > high)
                    {
                        high = value;
                    }
                }
            }

            if (progress is not null && Stopwatch.GetTimestamp() >= nextProgress)
            {
                progress(new AudioPeaks(minimum[..filled], maximum[..filled], filled, isComplete: false));
                nextProgress = Stopwatch.GetTimestamp() + every;
            }
        }

        if (window >= 0)
        {
            Store(ref minimum, ref maximum, window, low, high);
            filled = window + 1;
        }

        return new AudioPeaks(minimum[..filled], maximum[..filled], filled, isComplete: true);
    }

    private static void Store(ref sbyte[] minimum, ref sbyte[] maximum, int window, int low, int high)
    {
        if (window >= minimum.Length)
        {
            int grown = Math.Max(window + 1, minimum.Length * 2);
            Array.Resize(ref minimum, grown);
            Array.Resize(ref maximum, grown);
        }

        minimum[window] = (sbyte)low;
        maximum[window] = (sbyte)high;
    }
}
