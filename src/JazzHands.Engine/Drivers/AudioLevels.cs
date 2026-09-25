using System.Collections.Concurrent;
using JazzHands.Core.Drivers;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Media.Audio;
using Serilog;

namespace JazzHands.Engine.Drivers;

/// <summary>
/// How loud a track is over its sequence, in a band, for a driver's <c>audio()</c>: worked out
/// once from the track's clips and kept, shared by the preview and exports.
/// </summary>
/// <remarks>
/// The track's clips are read to mono at 24 kHz, split into bands by two-pole filters (low under
/// 200 Hz, high over 2 kHz, mid between), and measured as root mean square every 10 ms on the
/// sequence. An attack and release follow that, and the loudest point of the track is 1. The
/// first frame that asks waits for the read, which is a second or so for a song; it is synchronous
/// on purpose, so an export is the same every time. Clips that are reversed, remapped or frozen
/// are silent here.
/// </remarks>
public static class AudioLevels
{
    /// <summary>Envelope points a second.</summary>
    public const int Rate = 100;

    private const int SampleRate = 24000;

    private static readonly ILogger LogFor = Log.ForContext(typeof(AudioLevels));
    private static readonly ConcurrentDictionary<string, float[][]> Bands = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, float[]> Followed = new(StringComparer.Ordinal);

    /// <summary>The level at a moment of the sequence, 0 to 1.</summary>
    public static double At(Project project, Sequence sequence, string trackId, AudioBand band, double seconds, double attack, double release, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.Track(trackId) is not { } track)
        {
            return 0;
        }

        string key = Signature(project, track, projectPath);
        string followKey = FormattableString.Invariant($"{key}|{(int)band}|{attack:R}|{release:R}");
        float[] envelope = Followed.GetOrAdd(followKey, _ =>
        {
            float[][] bands = Bands.GetOrAdd(key, _ => Measure(project, track, projectPath));
            return Follow(bands[(int)band], attack, release);
        });

        double at = seconds * Rate;
        if (envelope.Length == 0 || at < 0)
        {
            return 0;
        }

        int index = (int)Math.Floor(at);
        if (index >= envelope.Length - 1)
        {
            return index == envelope.Length - 1 ? envelope[^1] : 0;
        }

        double fraction = at - index;
        return envelope[index] + ((envelope[index + 1] - envelope[index]) * fraction);
    }

    /// <summary>Forgets everything measured, for tests and when caches are cleared.</summary>
    public static void Clear()
    {
        Bands.Clear();
        Followed.Clear();
    }

    /// <summary>
    /// A follower over a band's levels: rising at the attack, falling at the release (seconds to
    /// cover most of the way), scaled so the loudest point is 1.
    /// </summary>
    public static float[] Follow(float[] levels, double attack, double release)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var followed = new float[levels.Length];
        double up = attack > 0 ? Math.Exp(-1.0 / (attack * Rate)) : 0;
        double down = release > 0 ? Math.Exp(-1.0 / (release * Rate)) : 0;
        double state = 0;
        double loudest = 0;
        for (int index = 0; index < levels.Length; index++)
        {
            double target = levels[index];
            double keep = target > state ? up : down;
            state = target + ((state - target) * keep);
            followed[index] = (float)state;
            loudest = Math.Max(loudest, state);
        }

        if (loudest > 0)
        {
            for (int index = 0; index < followed.Length; index++)
            {
                followed[index] = (float)(followed[index] / loudest);
            }
        }

        return followed;
    }

    /// <summary>What decides a track's sound: its clips, where they sit, and what they play.</summary>
    private static string Signature(Project project, Track track, string projectPath)
    {
        var text = new System.Text.StringBuilder(projectPath).Append('|').Append(track.Id);
        foreach (Clip clip in track.Clips)
        {
            string hash = clip.MediaId is { } media ? project.MediaItem(media)?.Hash ?? media : "-";
            text.Append('|').Append(hash).Append(':').Append(clip.SourceStreamIndex)
                .Append(':').Append(clip.Start.Value).Append(':').Append(clip.Duration.Value)
                .Append(':').Append(clip.SourceIn.Value).Append(':').Append(clip.EffectiveSpeed.ToString())
                .Append(':').Append(clip.Enabled).Append(':').Append(clip.Reverse).Append(':').Append(clip.IsRemapped).Append(':').Append(clip.IsHold);
        }

        return text.ToString();
    }

    /// <summary>The four bands' levels every 10 ms from the sequence's start to the track's end.</summary>
    private static float[][] Measure(Project project, Track track, string projectPath)
    {
        Flicks end = track.Clips.IsEmpty ? Flicks.Zero : track.Clips.Max(clip => clip.End);
        int points = (int)Math.Ceiling(end.ToSeconds() * Rate) + 1;
        float[][] bands = [new float[points], new float[points], new float[points], new float[points]];

        foreach (Clip clip in track.Clips)
        {
            if (!clip.Enabled || clip.Reverse || clip.IsRemapped || clip.IsHold
                || clip.MediaId is not { } mediaId
                || project.MediaItem(mediaId) is not { } item)
            {
                continue;
            }

            MediaStream? stream = track.Kind == TrackKind.Audio
                ? item.Info?.Streams.FirstOrDefault(candidate => candidate.Index == clip.SourceStreamIndex && candidate.Kind == MediaStreamKind.Audio)
                : item.Info?.AudioStreams.FirstOrDefault();
            string path = projectPath.Length == 0 ? Path.GetFullPath(item.RelativePath) : ProjectPaths.Resolve(projectPath, item.RelativePath);
            if (stream is null || !File.Exists(path))
            {
                continue;
            }

            float[] samples;
            try
            {
                samples = MonoReader.Read(path, stream.Index, clip.SourceIn, clip.SourceDuration, SampleRate);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
            {
                LogFor.Warning(error, "Could not read {Path} for a driver's audio", path);
                continue;
            }

            Measure(samples, clip.EffectiveSpeed.ToDouble(), clip.Start.ToSeconds(), clip.End.ToSeconds(), bands);
        }

        return bands;
    }

    /// <summary>One clip's sound measured into the bands at its place on the sequence.</summary>
    internal static void Measure(float[] samples, double speed, double start, double end, float[][] bands)
    {
        var low = Biquad.LowPass(200, SampleRate);
        var high = Biquad.HighPass(2000, SampleRate);
        var midLow = Biquad.HighPass(200, SampleRate);
        var midHigh = Biquad.LowPass(2000, SampleRate);

        // Each 10 ms of the sequence covers this many source samples.
        int window = Math.Max(1, (int)Math.Round(SampleRate / (double)Rate * speed));
        double[] sums = new double[4];
        int count = 0;
        int point = (int)Math.Round(start * Rate);
        int last = Math.Min(bands[0].Length - 1, (int)Math.Floor(end * Rate));
        for (int index = 0; index < samples.Length && point <= last; index++)
        {
            float sample = samples[index];
            float lo = low.Next(sample);
            float hi = high.Next(sample);
            float mid = midHigh.Next(midLow.Next(sample));
            sums[0] += sample * sample;
            sums[1] += lo * lo;
            sums[2] += mid * mid;
            sums[3] += hi * hi;
            if (++count < window)
            {
                continue;
            }

            if (point >= 0)
            {
                for (int band = 0; band < 4; band++)
                {
                    bands[band][point] = (float)Math.Sqrt(sums[band] / count);
                }
            }

            Array.Clear(sums);
            count = 0;
            point++;
        }
    }

    /// <summary>A two-pole filter (the audio cookbook's), one sample at a time.</summary>
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1;
        private double _x2;
        private double _y1;
        private double _y2;

        public static Biquad LowPass(double frequency, double rate)
        {
            (double cos, double alpha) = Shape(frequency, rate);
            double a0 = 1 + alpha;
            return new Biquad((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        public static Biquad HighPass(double frequency, double rate)
        {
            (double cos, double alpha) = Shape(frequency, rate);
            double a0 = 1 + alpha;
            return new Biquad((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        public float Next(float x)
        {
            double y = (b0 * x) + (b1 * _x1) + (b2 * _x2) - (a1 * _y1) - (a2 * _y2);
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return (float)y;
        }

        private static (double Cos, double Alpha) Shape(double frequency, double rate)
        {
            double w = 2 * Math.PI * frequency / rate;
            return (Math.Cos(w), Math.Sin(w) / (2 * 0.7071));
        }
    }
}
