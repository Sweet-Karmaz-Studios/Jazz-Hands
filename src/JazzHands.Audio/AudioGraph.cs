namespace JazzHands.Audio;

/// <summary>
/// Mixes a sequence, a block at a time, for playback and for export alike.
/// </summary>
/// <remarks>
/// <code>
/// source -> clip effects -> clip gain, fades, pan -> track bus -> track effects -> track gain, pan, mute, solo, meter
///   -> master -> master gain -> true peak limiter -> meter
/// </code>
/// <para>
/// What it mixes is a <see cref="MixSnapshot"/>, published from any thread and picked up at the
/// start of the next block, so an edit never lands halfway through one. Everything else here is
/// state that only the pulling thread touches: the scratch buffers, the limiter, the meters and
/// where each track's gain ended the last block. Gains and effect settings ramp from where the
/// last block ended to where the new snapshot says, across one block, so a fader moved in the
/// mixer is heard within one block and never as a click.
/// </para>
/// <para>
/// The master limiter looks 5 ms ahead and so is 5 ms late (<see cref="TruePeakLimiter"/>). The
/// graph takes that up by mixing 5 ms ahead of what it is asked for: output sample n is timeline
/// sample n. After a jump (a seek, the first pull, a cut in an export) it first mixes the 5 ms
/// before the limiter's output starts, so what comes out is the timeline from the first sample.
/// </para>
/// <para>
/// <see cref="Pull"/> is the audio thread's whole job during playback, so it follows the audio
/// thread's rules: no lock, no await, no log, no allocation. Every buffer is made in the
/// constructor, and anything a new snapshot needs (a track's meter) in <see cref="Publish"/>.
/// The one thing it cannot promise is that the samples are ready; the source writes silence for
/// what is not, and <see cref="StarvedBlocks"/> counts it. Export pulls the same graph from the
/// start of the range to the end, so what is heard is what is written.
/// </para>
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
    private readonly object _stripsGate = new();
    private Dictionary<string, StripState> _strips = new(StringComparer.Ordinal);
    private MixSnapshot _published;
    private long _starved;
    private long _blocks;
    private long _next = long.MinValue;
    private float _masterLast;
    private bool _masterValid;

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

        Limiter = new TruePeakLimiter(sampleRate, channels);
        Meter = new Meter(sampleRate, channels, loudness: true);
    }

    /// <summary>The mix rate.</summary>
    public int SampleRate { get; }

    /// <summary>The mix channel count.</summary>
    public int Channels { get; }

    /// <summary>The last on the master. Its settings come from the snapshot each block.</summary>
    public TruePeakLimiter Limiter { get; }

    /// <summary>After the limiter, so it shows what leaves: peak, RMS, true peak and loudness.</summary>
    public Meter Meter { get; }

    /// <summary>How far ahead the graph mixes to make up the limiter's lookahead, in samples.</summary>
    public int Latency => Limiter.Latency;

    /// <summary>The snapshot the next block will mix.</summary>
    public MixSnapshot Snapshot => Volatile.Read(ref _published);

    /// <summary>Blocks in which some source was not decoded in time and played as silence.</summary>
    public long StarvedBlocks => Volatile.Read(ref _starved);

    /// <summary>Blocks mixed since the graph was made.</summary>
    public long BlocksProcessed => Volatile.Read(ref _blocks);

    /// <summary>
    /// Hands the graph a new mix, which takes effect from the next block. Any thread.
    /// </summary>
    /// <remarks>
    /// Each track is given the state it had in the last snapshot (where its gain ended, its
    /// meter), or new state when it is new, before the snapshot is visible to the audio thread.
    /// </remarks>
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

        lock (_stripsGate)
        {
            var strips = new Dictionary<string, StripState>(StringComparer.Ordinal);
            foreach (TrackMix track in snapshot.TrackArray)
            {
                if (!_strips.TryGetValue(track.Id, out StripState? strip))
                {
                    strip = new StripState(SampleRate, Channels);
                }

                track.Strip = strip;
                strips[track.Id] = strip;
            }

            _strips = strips;
        }

        Volatile.Write(ref _published, snapshot);
    }

    /// <summary>A track's meter readings, after its volume and pan, or null for a track this graph is not mixing.</summary>
    public MeterRing? TrackMeter(string trackId)
    {
        lock (_stripsGate)
        {
            return _strips.TryGetValue(trackId, out StripState? strip) ? strip.Meter.Readings : null;
        }
    }

    /// <summary>
    /// Forgets the limiter, the meters' held peaks, every effect's state and where every gain was,
    /// for a seek. Call from the pulling thread, between pulls.
    /// </summary>
    public void Reset()
    {
        Meter.Reset();
        _next = long.MinValue;
        Forget(meters: true);
    }

    /// <summary>Forgets the limiter, where every gain was and every effect's state; the meters' too when asked.</summary>
    private void Forget(bool meters)
    {
        Limiter.Reset();
        _masterValid = false;

        // A filter's memory of where the playhead was is wrong where it is now.
        foreach (TrackMix track in Volatile.Read(ref _published).TrackArray)
        {
            if (track.Strip is { } strip)
            {
                strip.Valid = false;
                if (meters)
                {
                    strip.Meter.Reset();
                }
            }

            foreach (Effects.AudioEffectSlot effect in track.EffectArray)
            {
                effect.Effect.ResetAll();
            }

            foreach (ClipMix clip in track.ClipArray)
            {
                foreach (Effects.AudioEffectSlot effect in clip.EffectArray)
                {
                    effect.Effect.ResetAll();
                }
            }
        }
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

        if (startSample != _next)
        {
            Prime(startSample);
        }

        while (frames > 0)
        {
            int count = Math.Min(BlockSize, frames);
            MixBlock(startSample + Latency, count);
            Limiter.Process(_master, 0, count);
            Meter.Process(_master, 0, count, startSample, Limiter.LastReductionDb);

            for (int channel = 0; channel < Channels; channel++)
            {
                _master.Plane(channel, 0, count).CopyTo(output.Plane(channel, offset, count));
            }

            startSample += count;
            offset += count;
            frames -= count;
        }

        _next = startSample;
    }

    /// <summary>
    /// After a jump: forgets what was playing (a jump is a cut, with no ramp from the old place
    /// and no filter memory of it), then fills the limiter's lookahead with the mix just before
    /// where the output starts, so the first sample out is the timeline's first sample.
    /// </summary>
    private void Prime(long startSample)
    {
        Forget(meters: false);
        int remaining = Latency;
        long at = startSample;
        while (remaining > 0)
        {
            int count = Math.Min(BlockSize, remaining);
            MixBlock(at, count);
            Limiter.Process(_master, 0, count);
            at += count;
            remaining -= count;
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
            bool audible = track.IsAudible(snapshot.AnySolo);
            StripState? strip = track.Strip;

            // A track that has just been muted still plays this block, fading to nothing; once it
            // is silent it is skipped, keeping its silence so an unmute ramps up from nothing.
            if (!audible && (strip is null || !strip.Valid || Silent(strip.Last, Channels)))
            {
                if (strip is not null)
                {
                    Array.Clear(strip.Last);
                    strip.Valid = true;
                }

                continue;
            }

            ClipMix[] clips = track.ClipArray;
            bool any = false;
            int first = FirstEndingAfter(clips, start);

            for (int index = first; index < clips.Length; index++)
            {
                ClipMix clip = clips[index];
                if (clip.PlayStart >= end)
                {
                    break;
                }

                if (!any)
                {
                    _bus.Clear(0, frames);
                    any = true;
                }

                long from = Math.Max(start, clip.PlayStart);
                long to = Math.Min(end, clip.PlayEnd);
                if (from >= to)
                {
                    continue;
                }

                starved |= !MixClip(clip, from - clip.Start, (int)(from - start), (int)(to - from));
            }

            // After the last clip, a track's effects go on sounding for their tail: a reverb's
            // decay, a delay's echoes.
            if (!any && track.TailSamples > 0 && track.EffectArray.Length > 0 && first > 0
                && start < clips[first - 1].PlayEnd + track.TailSamples)
            {
                _bus.Clear(0, frames);
                any = true;
            }

            if (any)
            {
                foreach (Effects.AudioEffectSlot effect in track.EffectArray)
                {
                    effect.Process(_bus, 0, frames, Channels, SampleRate, start);
                }

                AddBus(track, strip, audible, start, frames);
            }
            else
            {
                strip?.Valid = false;
            }
        }

        ApplyMaster(snapshot.Master, start, frames);

        if (starved)
        {
            Interlocked.Increment(ref _starved);
        }

        Interlocked.Increment(ref _blocks);
    }

    /// <summary>
    /// Applies a track's gain and balance to its bus, meters it and adds it to the master. The gain
    /// ramps from where the last block ended, so a change of snapshot (a fader, a mute) is a ramp.
    /// </summary>
    private void AddBus(TrackMix track, StripState? strip, bool audible, long start, int frames)
    {
        float gainTo = audible ? Dsp.DbToGain(track.Volume.Evaluate(start + frames)) : 0.0f;
        Dsp.Balance(track.Pan.Evaluate(start + frames), out float leftTo, out float rightTo);

        bool continuing = strip is { Valid: true };
        float gainFrom = 0.0f, leftFrom = 0.0f, rightFrom = 0.0f;
        if (!continuing)
        {
            gainFrom = audible ? Dsp.DbToGain(track.Volume.Evaluate(start)) : 0.0f;
            Dsp.Balance(track.Pan.Evaluate(start), out leftFrom, out rightFrom);
        }

        for (int channel = 0; channel < Channels; channel++)
        {
            float from = continuing ? strip!.Last[channel] : gainFrom * Dsp.SideGain(channel, Channels, leftFrom, rightFrom);
            float to = gainTo * Dsp.SideGain(channel, Channels, leftTo, rightTo);
            Scale(_bus.Plane(channel, 0, frames), from, to);
            Add(_bus.Plane(channel, 0, frames), _master.Plane(channel, 0, frames));

            strip?.Last[channel] = to;
        }

        if (strip is not null)
        {
            strip.Valid = true;
            strip.Meter.Process(_bus, 0, frames, start);
        }
    }

    /// <summary>The master's volume, ramped like a track's, and the limiter's settings for the block.</summary>
    private void ApplyMaster(MasterMix master, long start, int frames)
    {
        float to = Dsp.DbToGain(master.Volume.Evaluate(start + frames));
        float from = _masterValid ? _masterLast : Dsp.DbToGain(master.Volume.Evaluate(start));
        if (from != 1.0f || to != 1.0f)
        {
            for (int channel = 0; channel < Channels; channel++)
            {
                Scale(_master.Plane(channel, 0, frames), from, to);
            }
        }

        _masterLast = to;
        _masterValid = true;
        Limiter.Enabled = master.LimiterEnabled;
        Limiter.CeilingDb = master.CeilingDb;
    }

    /// <summary>
    /// Mixes part of one clip into the track bus.
    /// </summary>
    /// <returns>False when the source was not ready in time.</returns>
    private bool MixClip(ClipMix clip, long clipSample, int offset, int frames)
    {
        int sourceChannels = clip.Source.Channels;
        bool ready = FetchSource(clip, clipSample, frames);

        // Clip effects run on the source channels, before gain, fades and pan: ClipSource, ClipFx, then the rest.
        foreach (Effects.AudioEffectSlot effect in clip.EffectArray)
        {
            effect.Process(_clipSource, 0, frames, sourceChannels, SampleRate, clipSample);
        }

        // Clip gain is ramped across the block so an automated fader does not step every 10 ms,
        // and the fades are exact per sample because a short one would be audibly stepped.
        float volumeFrom = Dsp.DbToGain(clip.Volume.Evaluate(clipSample));
        float volumeTo = clip.Volume.IsConstant ? volumeFrom : Dsp.DbToGain(clip.Volume.Evaluate(clipSample + frames));
        bool fading = clipSample < clip.FadeInLength || clip.Length - (clipSample + frames) < clip.FadeOutLength;
        bool crossfading = clip.Crossfading(clipSample, frames);
        Span<float> gains = _gains.AsSpan(0, frames);

        for (int index = 0; index < frames; index++)
        {
            float gain = volumeFrom + ((volumeTo - volumeFrom) * index / frames);
            gain = fading ? gain * clip.FadeGain(clipSample + index) : gain;
            gains[index] = crossfading ? gain * clip.CrossfadeGain(clipSample + index) : gain;
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

    /// <summary>Multiplies a plane by a gain ramped linearly across it.</summary>
    private static void Scale(Span<float> samples, float gainFrom, float gainTo)
    {
        int frames = samples.Length;
        if (gainFrom == gainTo)
        {
            if (gainFrom == 1.0f)
            {
                return;
            }

            for (int index = 0; index < frames; index++)
            {
                samples[index] *= gainFrom;
            }

            return;
        }

        for (int index = 0; index < frames; index++)
        {
            samples[index] *= gainFrom + ((gainTo - gainFrom) * index / frames);
        }
    }

    private static void Add(ReadOnlySpan<float> from, Span<float> into)
    {
        for (int index = 0; index < from.Length; index++)
        {
            into[index] += from[index];
        }
    }

    private static bool Silent(float[] gains, int channels)
    {
        for (int channel = 0; channel < channels; channel++)
        {
            if (gains[channel] != 0.0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The first clip heard after a sample. Clips on a track do not overlap, and a clip plays into
    /// a transition no further than the next one ends, so where they stop being heard is sorted too.
    /// </summary>
    private static int FirstEndingAfter(ClipMix[] clips, long sample)
    {
        int low = 0;
        int high = clips.Length;

        while (low < high)
        {
            int middle = (low + high) / 2;
            if (clips[middle].PlayEnd <= sample)
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
