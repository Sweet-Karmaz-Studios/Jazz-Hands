using JazzHands.Audio.Effects;
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
    /// <param name="sampleRate">The mix rate.</param>
    /// <param name="channels">The mix's channels.</param>
    /// <param name="tracks">The audio tracks, in stacking order.</param>
    /// <param name="master">The master bus; unity with the limiter on at -1 dBTP when null.</param>
    public MixSnapshot(int sampleRate, int channels, IReadOnlyList<TrackMix> tracks, MasterMix? master = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, Dsp.MaxChannels);
        ArgumentNullException.ThrowIfNull(tracks);

        SampleRate = sampleRate;
        Channels = channels;
        TrackArray = [.. tracks];
        Master = master ?? MasterMix.Default;

        foreach (TrackMix track in TrackArray)
        {
            AnySolo |= track.Solo;

            foreach (ClipMix clip in track.ClipArray)
            {
                EndSample = Math.Max(EndSample, clip.PlayEnd);
            }
        }
    }

    /// <summary>The master bus: its volume and its limiter.</summary>
    public MasterMix Master { get; }

    /// <summary>The mix rate.</summary>
    public int SampleRate { get; }

    /// <summary>How many channels the mix has.</summary>
    public int Channels { get; }

    /// <summary>The audio tracks, in stacking order.</summary>
    public IReadOnlyList<TrackMix> Tracks => TrackArray;

    /// <summary>True when any track is soloed, which silences every track that is not.</summary>
    public bool AnySolo { get; }

    /// <summary>The first sample after the last clip, and any tail it plays into a transition.</summary>
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
                if (clip.Generator is not null)
                {
                    continue;
                }

                long from = Math.Max(startSample, clip.PlayStart);
                long to = Math.Min(end, clip.PlayEnd);

                if (from < to)
                {
                    (long first, long count) = clip.SourceWindow(from - clip.Start + clip.Latency, (int)Math.Min(to - from, int.MaxValue));
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
    /// <param name="effects">The track's effects, run on its bus before its volume and pan.</param>
    /// <param name="tailSamples">How long its effects go on sounding after its last clip, in samples.</param>
    public TrackMix(string id, string name, bool muted, bool solo, ScalarCurve volume, ScalarCurve pan, IReadOnlyList<ClipMix> clips, IReadOnlyList<AudioEffectSlot>? effects = null, long tailSamples = 0)
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
        EffectArray = effects is null ? [] : [.. effects];
        TailSamples = Math.Max(0, tailSamples);
    }

    /// <summary>How long its effects go on sounding after its last clip ends: a reverb's decay, a delay's echoes.</summary>
    public long TailSamples { get; }

    /// <summary>
    /// What carries over between blocks for this track in one graph: the gain it ended the last
    /// block at, and its meter. Given by the graph when the snapshot is published, before the
    /// audio thread can see it, and kept for the track across snapshots.
    /// </summary>
    internal StripState? Strip { get; set; }

    /// <summary>
    /// Where the track's sound after its fader goes each block for another track's effect to listen
    /// to (a ducker keyed by it); null when nothing listens.
    /// </summary>
    public AudioBuffer? KeyOut { get; internal set; }

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

    /// <summary>The track's effects, first to last, over sequence time in samples.</summary>
    public IReadOnlyList<AudioEffectSlot> Effects => EffectArray;

    internal AudioEffectSlot[] EffectArray { get; }

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
        AudioChannelMap channelMap = AudioChannelMap.Auto,
        IReadOnlyList<AudioEffectSlot>? effects = null,
        long leadIn = 0,
        long tail = 0,
        Crossfade? crossIn = null,
        Crossfade? crossOut = null,
        long[]? remap = null)
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
        EffectArray = effects is null ? [] : [.. effects];
        foreach (AudioEffectSlot slot in EffectArray)
        {
            Latency += slot.Effect.LatencySamples;
        }

        LeadIn = Math.Max(0, leadIn);
        Tail = Math.Max(0, tail);
        CrossIn = crossIn;
        CrossOut = crossOut;
        Remap = remap;
    }

    /// <summary>Clip samples between the entries of a <see cref="Remap"/> table.</summary>
    public const int RemapStep = 256;

    /// <summary>
    /// For a time remapped clip, the source position (over <see cref="SpeedDen"/>) every
    /// <see cref="RemapStep"/> clip samples, from the clip's start; null for a clip at one speed.
    /// Between entries the position runs in a straight line, so the sound follows the speed curve
    /// the way tape would, its pitch with it.
    /// </summary>
    public long[]? Remap { get; }

    /// <summary>How many samples before <see cref="Start"/> it plays, into a transition from the clip before it.</summary>
    public long LeadIn { get; }

    /// <summary>How many samples after <see cref="End"/> it plays, into a transition to the clip after it.</summary>
    public long Tail { get; }

    /// <summary>The first sample it is heard at: its start, less its lead in.</summary>
    public long PlayStart => Start - LeadIn;

    /// <summary>The first sample after it is heard: its end, plus its tail.</summary>
    public long PlayEnd => End + Tail;

    /// <summary>The crossfade it comes in on, from the clip before it, or null for none.</summary>
    public Crossfade? CrossIn { get; }

    /// <summary>The crossfade it goes out on, into the clip after it, or null for none.</summary>
    public Crossfade? CrossOut { get; }

    /// <summary>True when any sample from <paramref name="clipSample"/> for <paramref name="frames"/> is in a crossfade.</summary>
    public bool Crossfading(long clipSample, int frames)
    {
        long from = Start + clipSample;
        long to = from + frames;
        return (CrossIn is { } entering && from < entering.End && to > entering.Start)
            || (CrossOut is { } leaving && from < leaving.End && to > leaving.Start);
    }

    /// <summary>The crossfade gain at a clip sample: rising through the crossfade in, falling through the one out, 1 elsewhere.</summary>
    public float CrossfadeGain(long clipSample)
    {
        long sample = Start + clipSample;
        float gain = 1.0f;

        if (CrossIn is { } entering && sample < entering.End)
        {
            gain = JazzHands.Audio.Effects.Crossfades.Gain(entering.Curve, entering.Progress(sample));
        }

        if (CrossOut is { } leaving && sample >= leaving.Start)
        {
            gain *= JazzHands.Audio.Effects.Crossfades.Gain(leaving.Curve, 1.0f - leaving.Progress(sample));
        }

        return gain;
    }

    /// <summary>
    /// The clip's effects, first to last, run on its source channels before its gain, fades and
    /// pan, over clip time in samples.
    /// </summary>
    public IReadOnlyList<AudioEffectSlot> Effects => EffectArray;

    internal AudioEffectSlot[] EffectArray { get; }

    /// <summary>How many samples late its effects hand the sound back, together: the mixer reads its file this far ahead.</summary>
    public int Latency { get; }

    /// <summary>What makes the clip's sound when it is a generator (a test tone) rather than a file; null for a file.</summary>
    public AudioGenerator? Generator { get; init; }

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
    public bool IsStraight => Remap is null && SpeedNum == SpeedDen && !Reverse;

    /// <summary>The source position of a clip sample, as a numerator over <see cref="SpeedDen"/>.</summary>
    public long SourcePosition(long clipSample)
    {
        if (Remap is { Length: > 1 } table)
        {
            long index = Math.Clamp(Dsp.FloorDiv(clipSample, RemapStep), 0, table.Length - 2);
            long offset = clipSample - (index * RemapStep);
            return table[index] + ((table[index + 1] - table[index]) * offset / RemapStep);
        }

        return Straight(clipSample);
    }

    private long Straight(long clipSample) => Reverse
        ? (SourceOut * SpeedDen) - ((clipSample + 1) * SpeedNum)
        : (SourceIn * SpeedDen) + (clipSample * SpeedNum);

    /// <summary>
    /// The source samples a run of clip samples reads, including the one past the end that
    /// interpolation needs.
    /// </summary>
    public (long First, long Count) SourceWindow(long clipSample, int frames)
    {
        // A stretched clip is read in its own samples, which the engine renders.
        if (Source.Stretch is not null)
        {
            return (clipSample, frames);
        }

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

    /// <summary>The fade gain at a clip sample, both ends multiplied. A fade is the clip's own, so its lead in and tail are at full level.</summary>
    public float FadeGain(long clipSample)
    {
        float gain = 1.0f;
        if (clipSample < 0 || clipSample >= Length)
        {
            return gain;
        }

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

/// <summary>A crossfade between two clips on a track, in samples on the timeline.</summary>
/// <param name="Start">The first sample of it.</param>
/// <param name="Length">How many samples it lasts.</param>
/// <param name="Curve">The shape of its gains.</param>
public readonly record struct Crossfade(long Start, long Length, Effects.CrossfadeCurve Curve)
{
    /// <summary>The first sample after it.</summary>
    public long End => Start + Length;

    /// <summary>How far through it a sample is, 0 to 1.</summary>
    public float Progress(long sample) => Length <= 0 ? 1.0f : Math.Clamp((float)((double)(sample - Start) / Length), 0.0f, 1.0f);
}

/// <summary>The master bus of a snapshot: its volume and the true peak limiter that ends the mix.</summary>
/// <param name="Volume">Gain in decibels over sequence time in samples.</param>
/// <param name="LimiterEnabled">False to let the mix through without limiting (still as late, so nothing moves).</param>
/// <param name="CeilingDb">The limiter's ceiling in dBTP.</param>
public sealed record MasterMix(ScalarCurve Volume, bool LimiterEnabled = true, float CeilingDb = -1.0f)
{
    /// <summary>Unity, with the limiter on at -1 dBTP.</summary>
    public static MasterMix Default { get; } = new(ScalarCurve.Constant(0.0f));
}

/// <summary>What one graph keeps between blocks for one track: where its gain ended, and its meter.</summary>
internal sealed class StripState(int sampleRate, int channels)
{
    /// <summary>The gain each mix channel ended the last block at.</summary>
    public float[] Last { get; } = new float[Dsp.MaxChannels];

    /// <summary>True once <see cref="Last"/> holds a block's end; false after a seek.</summary>
    public bool Valid { get; set; }

    /// <summary>After the track's volume and pan.</summary>
    public Meter Meter { get; } = new(sampleRate, channels);
}
