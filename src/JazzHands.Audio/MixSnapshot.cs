using JazzHands.Core.Model;

namespace JazzHands.Audio;

/// <summary>
/// Everything the graph needs to mix a sequence, frozen at one moment.
/// </summary>
/// <remarks>
/// The editor changes the project from the UI thread and the audio thread mixes it, and the two
/// must never meet. So a change builds a new snapshot, publishes it with one reference write, and
/// the graph picks it up at the start of its next block. Nothing in a snapshot changes after it
/// is built, which is what makes reading it without a lock safe.
///
/// Positions are sample indexes at the mix rate, converted once from Flicks when the snapshot is
/// built, so the audio thread does no time conversion at all.
/// </remarks>
public sealed class MixSnapshot
{
    /// <summary>Creates a snapshot.</summary>
    public MixSnapshot(int sampleRate, int channels, IReadOnlyList<TrackMix> tracks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, Dsp.MaxChannels);
        ArgumentNullException.ThrowIfNull(tracks);

        SampleRate = sampleRate;
        Channels = channels;
        TrackArray = [.. tracks];

        foreach (TrackMix track in TrackArray)
        {
            AnySolo |= track.Solo;

            foreach (ClipMix clip in track.ClipArray)
            {
                EndSample = Math.Max(EndSample, clip.End);
            }
        }
    }

    /// <summary>The mix rate.</summary>
    public int SampleRate { get; }

    /// <summary>How many channels the mix has.</summary>
    public int Channels { get; }

    /// <summary>The audio tracks, in stacking order.</summary>
    public IReadOnlyList<TrackMix> Tracks => TrackArray;

    /// <summary>True when any track is soloed, which silences every track that is not.</summary>
    public bool AnySolo { get; }

    /// <summary>The first sample after the last clip.</summary>
    public long EndSample { get; }

    /// <summary>An array rather than a list, so the audio thread iterates without an enumerator.</summary>
    internal TrackMix[] TrackArray { get; }

    /// <summary>A snapshot with nothing in it, for a graph that has not been given a sequence.</summary>
    public static MixSnapshot Silent(int sampleRate, int channels) => new(sampleRate, channels, []);

    /// <summary>
    /// The source ranges a stretch of the mix will read, for decoding them before they are needed.
    /// </summary>
    /// <remarks>
    /// Only what can be heard: a muted track, a track silenced by someone else's solo, and a clip
    /// outside the range are left out, so decoding ahead never spends time on something the graph
    /// will skip.
    /// </remarks>
    public void CollectDemands(long startSample, long frames, ICollection<SourceDemand> demands)
    {
        ArgumentNullException.ThrowIfNull(demands);

        long end = startSample + frames;

        foreach (TrackMix track in TrackArray)
        {
            if (!track.IsAudible(AnySolo))
            {
                continue;
            }

            foreach (ClipMix clip in track.ClipArray)
            {
                long from = Math.Max(startSample, clip.Start);
                long to = Math.Min(end, clip.End);

                if (from < to)
                {
                    (long first, long count) = clip.SourceWindow(from - clip.Start, (int)Math.Min(to - from, int.MaxValue));
                    demands.Add(new SourceDemand(clip.Source, first, count));
                }
            }
        }
    }
}

/// <summary>One audio track of a snapshot.</summary>
public sealed class TrackMix
{
    /// <summary>Creates a track.</summary>
    /// <param name="id">The track identifier.</param>
    /// <param name="name">Its name, for meters and logs.</param>
    /// <param name="muted">Muted tracks contribute nothing.</param>
    /// <param name="solo">When any track is soloed, only soloed tracks contribute.</param>
    /// <param name="volume">Gain in decibels over sequence time.</param>
    /// <param name="pan">Balance from -1 to 1 over sequence time.</param>
    /// <param name="clips">Clips, sorted by start.</param>
    public TrackMix(string id, string name, bool muted, bool solo, ScalarCurve volume, ScalarCurve pan, IReadOnlyList<ClipMix> clips)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(pan);
        ArgumentNullException.ThrowIfNull(clips);

        Id = id;
        Name = name ?? string.Empty;
        Muted = muted;
        Solo = solo;
        Volume = volume;
        Pan = pan;
        ClipArray = [.. clips.OrderBy(clip => clip.Start)];
    }

    /// <summary>The track identifier.</summary>
    public string Id { get; }

    /// <summary>Its name.</summary>
    public string Name { get; }

    /// <summary>True when the track is muted.</summary>
    public bool Muted { get; }

    /// <summary>True when the track is soloed.</summary>
    public bool Solo { get; }

    /// <summary>Gain in decibels, over sequence time in samples.</summary>
    public ScalarCurve Volume { get; }

    /// <summary>Balance, over sequence time in samples.</summary>
    public ScalarCurve Pan { get; }

    /// <summary>The clips, sorted by start.</summary>
    public IReadOnlyList<ClipMix> Clips => ClipArray;

    internal ClipMix[] ClipArray { get; }

    /// <summary>Whether this track is heard, given whether anything is soloed.</summary>
    public bool IsAudible(bool anySolo) => !Muted && (!anySolo || Solo);
}

/// <summary>
/// One clip of a snapshot, with its timing already in samples.
/// </summary>
/// <remarks>
/// The source position of the k-th sample of the clip is a rational, kept as a numerator over
/// <see cref="SpeedDen"/> so that a clip at 1001/1000 speed does not drift the way one stepped in
/// floating point would. Forwards it is <c>SourceIn + k * speed</c>; reversed it counts down from
/// <see cref="SourceOut"/>, one sample in, because a source range is half open and its end is one
/// past the last sample there is.
/// </remarks>
public sealed class ClipMix
{
    /// <summary>Creates a clip.</summary>
    public ClipMix(
        string id,
        AudioSourceRef source,
        long start,
        long end,
        long sourceIn,
        long sourceOut,
        long speedNum = 1,
        long speedDen = 1,
        bool reverse = false,
        long fadeInLength = 0,
        Interp fadeInCurve = Interp.Linear,
        long fadeOutLength = 0,
        Interp fadeOutCurve = Interp.Linear,
        ScalarCurve? volume = null,
        ScalarCurve? pan = null,
        AudioChannelMap channelMap = AudioChannelMap.Auto)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.Channels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(source.Channels, Dsp.MaxChannels);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speedNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speedDen);

        Id = id;
        Source = source;
        Start = start;
        End = end;
        SourceIn = sourceIn;
        SourceOut = sourceOut;
        SpeedNum = speedNum;
        SpeedDen = speedDen;
        Reverse = reverse;
        FadeInLength = Math.Max(0, fadeInLength);
        FadeInCurve = fadeInCurve;
        FadeOutLength = Math.Max(0, fadeOutLength);
        FadeOutCurve = fadeOutCurve;
        Volume = volume ?? ScalarCurve.Constant(0.0f);
        Pan = pan ?? ScalarCurve.Constant(0.0f);
        ChannelMap = channelMap;
    }

    /// <summary>The clip identifier.</summary>
    public string Id { get; }

    /// <summary>The stream it plays.</summary>
    public AudioSourceRef Source { get; }

    /// <summary>The first sample on the timeline.</summary>
    public long Start { get; }

    /// <summary>The first sample after it on the timeline.</summary>
    public long End { get; }

    /// <summary>How many samples it occupies.</summary>
    public long Length => End - Start;

    /// <summary>Where it starts in the source.</summary>
    public long SourceIn { get; }

    /// <summary>The first source sample after it.</summary>
    public long SourceOut { get; }

    /// <summary>Speed numerator.</summary>
    public long SpeedNum { get; }

    /// <summary>Speed denominator.</summary>
    public long SpeedDen { get; }

    /// <summary>True when the source plays backwards.</summary>
    public bool Reverse { get; }

    /// <summary>How long the fade in lasts, in samples.</summary>
    public long FadeInLength { get; }

    /// <summary>The fade in's shape.</summary>
    public Interp FadeInCurve { get; }

    /// <summary>How long the fade out lasts, in samples.</summary>
    public long FadeOutLength { get; }

    /// <summary>The fade out's shape.</summary>
    public Interp FadeOutCurve { get; }

    /// <summary>Gain in decibels, over clip time in samples.</summary>
    public ScalarCurve Volume { get; }

    /// <summary>Pan, over clip time in samples.</summary>
    public ScalarCurve Pan { get; }

    /// <summary>Which of the source's channels play.</summary>
    public AudioChannelMap ChannelMap { get; }

    /// <summary>True when the source is read one for one, forwards, which is the fast path.</summary>
    public bool IsStraight => SpeedNum == SpeedDen && !Reverse;

    /// <summary>The source position of a clip sample, as a numerator over <see cref="SpeedDen"/>.</summary>
    public long SourcePosition(long clipSample) => Reverse
        ? (SourceOut * SpeedDen) - ((clipSample + 1) * SpeedNum)
        : (SourceIn * SpeedDen) + (clipSample * SpeedNum);

    /// <summary>
    /// The source samples a run of clip samples reads, including the one past the end that
    /// interpolation needs.
    /// </summary>
    public (long First, long Count) SourceWindow(long clipSample, int frames)
    {
        if (IsStraight)
        {
            return (SourceIn + clipSample, frames);
        }

        long a = SourcePosition(clipSample);
        long b = SourcePosition(clipSample + Math.Max(frames - 1, 0));
        long first = Dsp.FloorDiv(Math.Min(a, b), SpeedDen);
        long last = Dsp.FloorDiv(Math.Max(a, b), SpeedDen) + 1;

        return (first, last - first + 1);
    }

    /// <summary>The fade gain at a clip sample, both ends multiplied.</summary>
    public float FadeGain(long clipSample)
    {
        float gain = 1.0f;

        if (clipSample < FadeInLength)
        {
            gain = Dsp.FadeGain(FadeInCurve, (float)clipSample / FadeInLength);
        }

        long remaining = Length - clipSample;
        if (remaining <= FadeOutLength)
        {
            gain *= Dsp.FadeGain(FadeOutCurve, (float)(remaining - 1) / FadeOutLength);
        }

        return gain;
    }
}
