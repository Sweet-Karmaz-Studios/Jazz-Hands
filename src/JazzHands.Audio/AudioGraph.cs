namespace JazzHands.Audio;

/// <summary>
/// Mixes a sequence, a block at a time, for playback and for export alike.
/// </summary>
/// <remarks>
/// <code>
/// source -> clip gain, fades, pan -> track bus -> track gain, pan, mute, solo -> master -> limiter -> meter
/// </code>
/// What it mixes is a <see cref="MixSnapshot"/>, published from any thread and picked up at the
/// start of the next block, so an edit never lands halfway through one. Everything else here is
/// state that only the pulling thread touches: the scratch buffers, the limiter's release, the
/// meter's held peaks.
///
/// <see cref="Pull"/> is the audio thread's whole job during playback, so it follows the audio
/// thread's rules: no lock, no await, no log, no allocation. Every buffer is made in the
/// constructor. The one thing it cannot promise is that the samples are ready; the source writes
/// silence for what is not, and <see cref="StarvedBlocks"/> counts it.
///
/// Export pulls the same graph from the start of the range to the end, so what is heard is what
/// is written.
/// </remarks>
public sealed class AudioGraph
{
    /// <summary>Samples per block, per channel.</summary>
    public const int BlockSize = 512;

    /// <summary>
    /// How many source samples one read can span when a clip is not at normal speed. A clip at
    /// eight times speed needs 4096 source samples for one block, so faster clips read in parts.
    /// </summary>
    private const int WindowCapacity = 4096;

    private readonly IAudioSampleSource _source;
    private readonly AudioBuffer _master;
    private readonly AudioBuffer _bus;
    private readonly AudioBuffer _clipSource;
    private readonly AudioBuffer _window;
    private readonly float[] _gains = new float[BlockSize];
    private MixSnapshot _published;
    private long _starved;
    private long _blocks;

    /// <summary>Creates a graph.</summary>
    /// <param name="source">Where decoded samples come from.</param>
    /// <param name="sampleRate">The mix rate. A snapshot at another rate needs another graph.</param>
    /// <param name="channels">The mix channel count: 1, 2 or 6.</param>
    public AudioGraph(IAudioSampleSource source, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, Dsp.MaxChannels);

        _source = source;
        SampleRate = sampleRate;
        Channels = channels;

        _master = new AudioBuffer(channels, BlockSize);
        _bus = new AudioBuffer(channels, BlockSize);
        _clipSource = new AudioBuffer(Dsp.MaxChannels, BlockSize);
        _window = new AudioBuffer(Dsp.MaxChannels, WindowCapacity);
        _published = MixSnapshot.Silent(sampleRate, channels);

        Limiter = new Limiter(sampleRate);
        Meter = new Meter(sampleRate);
    }

    /// <summary>The mix rate.</summary>
    public int SampleRate { get; }

    /// <summary>The mix channel count.</summary>
    public int Channels { get; }

    /// <summary>The last on the master.</summary>
    public Limiter Limiter { get; }

    /// <summary>After the limiter, so it shows what leaves.</summary>
    public Meter Meter { get; }

    /// <summary>The snapshot the next block will mix.</summary>
    public MixSnapshot Snapshot => Volatile.Read(ref _published);

    /// <summary>Blocks in which some source was not decoded in time and played as silence.</summary>
    public long StarvedBlocks => Volatile.Read(ref _starved);

    /// <summary>Blocks mixed since the graph was made.</summary>
    public long BlocksProcessed => Volatile.Read(ref _blocks);

    /// <summary>
    /// Hands the graph a new mix, which takes effect from the next block. Any thread.
    /// </summary>
    public void Publish(MixSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SampleRate != SampleRate || snapshot.Channels != Channels)
        {
            throw new ArgumentException(
                $"The snapshot mixes {snapshot.Channels} channels at {snapshot.SampleRate} Hz and this graph "
                + $"{Channels} at {SampleRate} Hz. A change of format needs a new graph.",
                nameof(snapshot));
        }

        Volatile.Write(ref _published, snapshot);
    }

    /// <summary>
    /// Forgets the limiter's release and the meter's held peaks, for a seek. Call from the
    /// pulling thread, between pulls.
    /// </summary>
    public void Reset()
    {
        Limiter.Reset();
        Meter.Reset();
    }

    /// <summary>
    /// Mixes a stretch of the timeline into a buffer.
    /// </summary>
    /// <param name="startSample">The first sample, at the mix rate.</param>
    /// <param name="output">Where to write. Must have <see cref="Channels"/> planes.</param>
    /// <param name="offset">Where in each plane to start.</param>
    /// <param name="frames">How many samples. Mixed in blocks of <see cref="BlockSize"/>.</param>
    public void Pull(long startSample, AudioBuffer output, int offset, int frames)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(frames);

        if (output.Channels != Channels)
        {
            throw new ArgumentException($"The output has {output.Channels} channels and the mix {Channels}.", nameof(output));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + frames, output.Capacity, nameof(frames));

        while (frames > 0)
        {
            int count = Math.Min(BlockSize, frames);
            MixBlock(startSample, count);

            for (int channel = 0; channel < Channels; channel++)
            {
                _master.Plane(channel, 0, count).CopyTo(output.Plane(channel, offset, count));
            }

            startSample += count;
            offset += count;
            frames -= count;
        }
    }

    private void MixBlock(long start, int frames)
    {
        // Read once: this block mixes this snapshot, whatever is published while it runs.
        MixSnapshot snapshot = Volatile.Read(ref _published);
        long end = start + frames;
        bool starved = false;

        _master.Clear(0, frames);

        foreach (TrackMix track in snapshot.TrackArray)
        {
            if (!track.IsAudible(snapshot.AnySolo))
            {
                continue;
            }

            ClipMix[] clips = track.ClipArray;
            bool any = false;

            for (int index = FirstEndingAfter(clips, start); index < clips.Length; index++)
            {
                ClipMix clip = clips[index];
                if (clip.Start >= end)
                {
                    break;
                }

                if (!any)
                {
                    _bus.Clear(0, frames);
                    any = true;
                }

                long from = Math.Max(start, clip.Start);
                long to = Math.Min(end, clip.End);
                starved |= !MixClip(clip, from - clip.Start, (int)(from - start), (int)(to - from));
            }

            if (any)
            {
                AddBus(track, start, frames);
            }
        }

        Limiter.Process(_master, 0, frames);
        Meter.Process(_master, 0, frames, start);

        if (starved)
        {
            Interlocked.Increment(ref _starved);
        }

        Interlocked.Increment(ref _blocks);
    }

    /// <summary>Applies a track's gain and balance to its bus and adds it to the master.</summary>
    private void AddBus(TrackMix track, long start, int frames)
    {
        float gainFrom = Dsp.DbToGain(track.Volume.Evaluate(start));
        float gainTo = track.Volume.IsConstant ? gainFrom : Dsp.DbToGain(track.Volume.Evaluate(start + frames));

        Dsp.Balance(track.Pan.Evaluate(start), out float leftFrom, out float rightFrom);
        float leftTo = leftFrom;
        float rightTo = rightFrom;
        if (!track.Pan.IsConstant)
        {
            Dsp.Balance(track.Pan.Evaluate(start + frames), out leftTo, out rightTo);
        }

        for (int channel = 0; channel < Channels; channel++)
        {
            float from = gainFrom * Dsp.SideGain(channel, Channels, leftFrom, rightFrom);
            float to = gainTo * Dsp.SideGain(channel, Channels, leftTo, rightTo);
            Accumulate(_bus.Plane(channel, 0, frames), _master.Plane(channel, 0, frames), from, to);
        }
    }

    /// <summary>
    /// Mixes part of one clip into the track bus.
    /// </summary>
    /// <returns>False when the source was not ready in time.</returns>
    private bool MixClip(ClipMix clip, long clipSample, int offset, int frames)
    {
        int sourceChannels = clip.Source.Channels;
        bool ready = FetchSource(clip, clipSample, frames);

        // Clip gain is ramped across the block so an automated fader does not step every 10 ms,
        // and the fades are exact per sample because a short one would be audibly stepped.
        float volumeFrom = Dsp.DbToGain(clip.Volume.Evaluate(clipSample));
        float volumeTo = clip.Volume.IsConstant ? volumeFrom : Dsp.DbToGain(clip.Volume.Evaluate(clipSample + frames));
        bool fading = clipSample < clip.FadeInLength || clip.Length - (clipSample + frames) < clip.FadeOutLength;
        Span<float> gains = _gains.AsSpan(0, frames);

        for (int index = 0; index < frames; index++)
        {
            float gain = volumeFrom + ((volumeTo - volumeFrom) * index / frames);
            gains[index] = fading ? gain * clip.FadeGain(clipSample + index) : gain;
        }

        Span<float> matrixFrom = stackalloc float[Dsp.MaxChannels * Dsp.MaxChannels];
        Span<float> matrixTo = stackalloc float[Dsp.MaxChannels * Dsp.MaxChannels];
        Dsp.ChannelMatrix(sourceChannels, Channels, clip.Pan.Evaluate(clipSample), clip.ChannelMap, matrixFrom);

        bool panMoves = !clip.Pan.IsConstant;
        if (panMoves)
        {
            Dsp.ChannelMatrix(sourceChannels, Channels, clip.Pan.Evaluate(clipSample + frames), clip.ChannelMap, matrixTo);
        }

        for (int output = 0; output < Channels; output++)
        {
            Span<float> bus = _bus.Plane(output, offset, frames);

            for (int input = 0; input < sourceChannels; input++)
            {
                int cell = (output * sourceChannels) + input;
                float from = matrixFrom[cell];
                float to = panMoves ? matrixTo[cell] : from;

                if (from == 0.0f && to == 0.0f)
                {
                    continue;
                }

                ReadOnlySpan<float> samples = _clipSource.Plane(input, 0, frames);
                for (int index = 0; index < frames; index++)
                {
                    float weight = from + ((to - from) * index / frames);
                    bus[index] += weight * gains[index] * samples[index];
                }
            }
        }

        return ready;
    }

    /// <summary>
    /// Fills the clip scratch buffer with the source samples a run of clip samples plays.
    /// </summary>
    /// <remarks>
    /// At normal speed forwards that is one read. Otherwise each output sample sits between two
    /// source samples and is interpolated linearly between them, which pitches the sound with the
    /// speed the way tape does. Pitch preserving time stretch is the shuttle's job, later.
    /// </remarks>
    private bool FetchSource(ClipMix clip, long clipSample, int frames)
    {
        if (clip.IsStraight)
        {
            return _source.Read(clip.Source, clip.SourceIn + clipSample, _clipSource, 0, frames);
        }

        int channels = clip.Source.Channels;
        long den = clip.SpeedDen;
        long chunk = Math.Max(1, (WindowCapacity - 2) * den / clip.SpeedNum);
        bool ready = true;
        int done = 0;

        while (done < frames)
        {
            int count = (int)Math.Min(frames - done, chunk);
            (long first, long length) = clip.SourceWindow(clipSample + done, count);
            ready &= _source.Read(clip.Source, first, _window, 0, (int)length);

            for (int index = 0; index < count; index++)
            {
                long position = clip.SourcePosition(clipSample + done + index);
                long whole = Dsp.FloorDiv(position, den);
                float fraction = (float)(position - (whole * den)) / den;
                int at = (int)(whole - first);

                for (int channel = 0; channel < channels; channel++)
                {
                    Span<float> window = _window.Plane(channel);
                    float a = window[at];
                    float b = window[at + 1];
                    _clipSource.Plane(channel)[done + index] = a + ((b - a) * fraction);
                }
            }

            done += count;
        }

        return ready;
    }

    /// <summary>Adds a plane into another with a gain ramped linearly across it.</summary>
    private static void Accumulate(ReadOnlySpan<float> from, Span<float> into, float gainFrom, float gainTo)
    {
        int frames = from.Length;

        if (gainFrom == gainTo)
        {
            if (gainFrom == 0.0f)
            {
                return;
            }

            for (int index = 0; index < frames; index++)
            {
                into[index] += from[index] * gainFrom;
            }

            return;
        }

        for (int index = 0; index < frames; index++)
        {
            into[index] += from[index] * (gainFrom + ((gainTo - gainFrom) * index / frames));
        }
    }

    /// <summary>The first clip that ends after a sample. Clips on a track do not overlap, so their ends are sorted too.</summary>
    private static int FirstEndingAfter(ClipMix[] clips, long sample)
    {
        int low = 0;
        int high = clips.Length;

        while (low < high)
        {
            int middle = (low + high) / 2;
            if (clips[middle].End <= sample)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
