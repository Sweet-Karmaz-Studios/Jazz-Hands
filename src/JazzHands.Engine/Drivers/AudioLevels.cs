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
/// <para>
/// Each clip is measured on its own and kept by what it plays (the file, the stream, the stretch
/// of it and the speed), so after an edit only the clips whose sound changed are read again: a
/// move or a ripple reads nothing, a trim or a split reads the clips it cut. The track's levels
/// are then those clips laid at their places, which is cheap. Clip measurements are kept up to
/// <see cref="KeptPoints"/> points, the least recently used going first; a track keeps its last
/// few arrangements, not one per edit.
/// </para>
/// </remarks>
public static class AudioLevels
{
    /// <summary>Envelope points a second.</summary>
    public const int Rate = 100;

    private const int SampleRate = 24000;

    /// <summary>
    /// How many points of clip measurements are kept: four bands of 100 a second, so about 70
    /// hours of clips, in 64 MB.
    /// </summary>
    public const long KeptPoints = 16L * 1024 * 1024;

    /// <summary>How many arrangements of one track are kept: the preview's and an export's of an older version, and a little more.</summary>
    private const int KeptArrangements = 4;

    private static readonly ILogger LogFor = Log.ForContext(typeof(AudioLevels));
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, KeptClip> Clips = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<Arrangement>> Tracks = new(StringComparer.Ordinal);
    private static long _keptPoints;
    private static long _clock;
    private static readonly ConcurrentDictionary<string, long> ReadCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many clips of a project have been read from their files, for tests of what an edit reads again.</summary>
    public static long Reads(string projectPath) => ReadCounts.GetValueOrDefault(projectPath ?? string.Empty);

    /// <summary>The level at a moment of the sequence, 0 to 1.</summary>
    public static double At(Project project, Sequence sequence, string trackId, AudioBand band, double seconds, double attack, double release, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.Track(trackId) is not { } track)
        {
            return 0;
        }

        Arrangement arrangement = ArrangementOf(project, track, projectPath);
        string followKey = FormattableString.Invariant($"{(int)band}|{attack:R}|{release:R}");
        float[] envelope = arrangement.Followed.GetOrAdd(followKey, _ => Follow(arrangement.Bands[(int)band], attack, release));

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
        lock (Gate)
        {
            Clips.Clear();
            Tracks.Clear();
            _keptPoints = 0;
        }
    }

    /// <summary>
    /// The track's levels as it is now: one of its last few arrangements when nothing that decides
    /// its sound has changed, or its clips' kept measurements laid out again.
    /// </summary>
    private static Arrangement ArrangementOf(Project project, Track track, string projectPath)
    {
        string trackKey = projectPath + "|" + track.Id;
        string signature = Signature(project, track, projectPath);
        lock (Gate)
        {
            if (Tracks.TryGetValue(trackKey, out List<Arrangement>? known) && known.Find(candidate => candidate.Signature == signature) is { } found)
            {
                return found;
            }
        }

        // Laid out outside the lock: a clip read for the first time can take a second.
        var arrangement = new Arrangement(signature, Measure(project, track, projectPath));
        lock (Gate)
        {
            if (!Tracks.TryGetValue(trackKey, out List<Arrangement>? known))
            {
                known = [];
                Tracks[trackKey] = known;
            }

            known.RemoveAll(candidate => candidate.Signature == signature);
            known.Insert(0, arrangement);
            if (known.Count > KeptArrangements)
            {
                known.RemoveRange(KeptArrangements, known.Count - KeptArrangements);
            }
        }

        return arrangement;
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
            if (stream is null || ClipLevels(projectPath, item, stream.Index, clip) is not { } levels)
            {
                continue;
            }

            Place(levels, clip.Start.ToSeconds(), clip.End.ToSeconds(), bands);
        }

        return bands;
    }

    /// <summary>
    /// A clip's levels from its own start, kept by what it plays; read from its file the first time.
    /// Null when the file is not there or cannot be read, which is tried again next time.
    /// </summary>
    private static float[][]? ClipLevels(string projectPath, MediaItem item, int streamIndex, Clip clip)
    {
        double speed = clip.EffectiveSpeed.ToDouble();
        string key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{item.Hash}|{(item.Hash.Length == 0 ? item.RelativePath : string.Empty)}|{streamIndex}|{clip.SourceIn.Value}|{clip.SourceDuration.Value}|{speed:R}");
        lock (Gate)
        {
            if (Clips.TryGetValue(key, out KeptClip? kept))
            {
                kept.Used = ++_clock;
                return kept.Levels;
            }
        }

        string path = projectPath.Length == 0 ? Path.GetFullPath(item.RelativePath) : ProjectPaths.Resolve(projectPath, item.RelativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        float[] samples;
        try
        {
            samples = MonoReader.Read(path, streamIndex, clip.SourceIn, clip.SourceDuration, SampleRate);
            ReadCounts.AddOrUpdate(projectPath, 1, (_, count) => count + 1);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
        {
            LogFor.Warning(error, "Could not read {Path} for a driver's audio", path);
            return null;
        }

        // One point more than the clip's length can cover: where it is laid decides how many are used.
        int points = (int)Math.Ceiling(clip.Duration.ToSeconds() * Rate) + 2;
        float[][] measured = [new float[points], new float[points], new float[points], new float[points]];
        int written = Measure(samples, speed, 0, points - 1, measured);

        // Only what was measured: a point the sound ran out before is left as it was when laid.
        float[][] levels = [.. measured.Select(band => band[..written])];
        lock (Gate)
        {
            if (!Clips.ContainsKey(key))
            {
                Clips[key] = new KeptClip(levels) { Used = ++_clock };
                _keptPoints += 4L * written;
                while (_keptPoints > KeptPoints && Clips.Count > 1)
                {
                    KeyValuePair<string, KeptClip> oldest = Clips.MinBy(entry => entry.Value.Used);
                    Clips.Remove(oldest.Key);
                    _keptPoints -= 4L * oldest.Value.Levels[0].Length;
                }
            }
        }

        return levels;
    }

    /// <summary>
    /// A clip's levels laid into the track's at its place: the points from its start's to its
    /// end's, over what is there, as measuring it there would write them.
    /// </summary>
    internal static void Place(float[][] levels, double start, double end, float[][] bands)
    {
        int first = (int)Math.Round(start * Rate);
        int last = Math.Min(bands[0].Length - 1, (int)Math.Floor(end * Rate));
        int count = Math.Min(levels[0].Length, last - first + 1);
        int skip = Math.Max(0, -first);
        if (count - skip <= 0)
        {
            return;
        }

        for (int band = 0; band < 4; band++)
        {
            Array.Copy(levels[band], skip, bands[band], first + skip, count - skip);
        }
    }

    /// <summary>One clip's sound measured into the bands at its place on the sequence; returns how many points it wrote from its first.</summary>
    internal static int Measure(float[] samples, double speed, double start, double end, float[][] bands)
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

        return point - (int)Math.Round(start * Rate);
    }

    /// <summary>A clip's measured levels and when they were last used.</summary>
    private sealed class KeptClip(float[][] levels)
    {
        public float[][] Levels { get; } = levels;

        public long Used { get; set; }
    }

    /// <summary>A track's levels for one arrangement of its clips, and the followers made from them.</summary>
    private sealed class Arrangement(string signature, float[][] bands)
    {
        public string Signature { get; } = signature;

        public float[][] Bands { get; } = bands;

        public ConcurrentDictionary<string, float[]> Followed { get; } = new(StringComparer.Ordinal);
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
