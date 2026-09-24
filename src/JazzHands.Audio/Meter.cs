using System.Runtime.CompilerServices;

namespace JazzHands.Audio;

/// <summary>A level per channel, stored inline so a reading is a value with no array behind it.</summary>
[InlineArray(Dsp.MaxChannels)]
public struct ChannelLevels
{
    private float _element;
}

/// <summary>What the meter saw over one block.</summary>
public struct MeterReading
{
    /// <summary>The first sample of the block, at the mix rate.</summary>
    public long Sample;

    /// <summary>How many samples the block had.</summary>
    public int Frames;

    /// <summary>How many channels are filled in.</summary>
    public int Channels;

    /// <summary>The largest absolute sample per channel, linear.</summary>
    public ChannelLevels Peak;

    /// <summary>Root mean square per channel, linear.</summary>
    public ChannelLevels Rms;

    /// <summary>The held peak per channel, linear: the highest recent peak, kept for a moment so an eye can catch it.</summary>
    public ChannelLevels Hold;

    /// <summary>True when any sample reached full scale (0 dBFS) in the block.</summary>
    public bool Clipped;

    /// <summary>The largest reconstructed peak of any channel, linear; zero on a meter that does not measure it.</summary>
    public float TruePeak;

    /// <summary>Loudness over the last 400 ms, LUFS; negative infinity until there is any, or on a meter without loudness.</summary>
    public float Momentary;

    /// <summary>Loudness over the last 3 s, LUFS.</summary>
    public float ShortTerm;

    /// <summary>Gated loudness since it was last reset, LUFS.</summary>
    public float Integrated;

    /// <summary>How far the master limiter turned the block down, dB (0 or less).</summary>
    public float ReductionDb;
}

/// <summary>
/// Peak, RMS and held peak of the master, per block.
/// </summary>
/// <remarks>
/// Runs on the audio thread and hands its readings to the UI through <see cref="MeterRing"/>, so
/// the two never share anything but that. The loudness meter (EBU R128, K-weighted) arrives with
/// the DSP phase; peak and RMS are what the mixer panel needs to show that sound is moving.
/// </remarks>
public sealed class Meter
{
    private readonly long _holdSamples;
    private readonly float[] _hold = new float[Dsp.MaxChannels];
    private readonly long[] _holdUntil = new long[Dsp.MaxChannels];
    private readonly Loudness? _loudness;
    private readonly TruePeak[] _truePeaks;
    private int _resetIntegrated;

    /// <summary>Creates a meter.</summary>
    /// <param name="sampleRate">The mix rate.</param>
    /// <param name="channels">The channels it measures, for loudness weighting.</param>
    /// <param name="loudness">True to measure loudness (EBU R128) and true peak as well: the master's meter.</param>
    /// <param name="holdSeconds">How long a peak stays held before it falls back.</param>
    public Meter(int sampleRate, int channels = 2, bool loudness = false, double holdSeconds = 1.5)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        _holdSamples = (long)(holdSeconds * sampleRate);
        _loudness = loudness ? new Loudness(sampleRate, channels) : null;
        _truePeaks = loudness ? [.. Enumerable.Range(0, Math.Min(channels, Dsp.MaxChannels)).Select(_ => new TruePeak())] : [];
    }

    /// <summary>Asks for the integrated loudness to start again from the next block. Any thread.</summary>
    public void ResetIntegrated() => Volatile.Write(ref _resetIntegrated, 1);

    /// <summary>Where readings go.</summary>
    public MeterRing Readings { get; } = new();

    /// <summary>Measures a range of a buffer and publishes one reading for it.</summary>
    /// <param name="buffer">The samples.</param>
    /// <param name="offset">Where the block starts in the buffer.</param>
    /// <param name="frames">How long it is.</param>
    /// <param name="sample">Where it sits on the timeline, for the reading's time and the hold.</param>
    /// <param name="reductionDb">The limiter's gain reduction over the block, for the master's reading.</param>
    public void Process(AudioBuffer buffer, int offset, int frames, long sample, float reductionDb = 0.0f)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var reading = new MeterReading
        {
            Sample = sample,
            Frames = frames,
            Channels = Math.Min(buffer.Channels, Dsp.MaxChannels),
            Momentary = Loudness.Silent,
            ShortTerm = Loudness.Silent,
            Integrated = Loudness.Silent,
            ReductionDb = reductionDb,
        };

        for (int channel = 0; channel < reading.Channels; channel++)
        {
            ReadOnlySpan<float> plane = buffer.Plane(channel, offset, frames);
            float peak = 0.0f;
            double sum = 0.0;

            foreach (float value in plane)
            {
                peak = Math.Max(peak, Math.Abs(value));
                sum += (double)value * value;
            }

            reading.Peak[channel] = peak;
            reading.Rms[channel] = frames == 0 ? 0.0f : (float)Math.Sqrt(sum / frames);

            if (peak >= _hold[channel] || sample >= _holdUntil[channel] || sample + _holdSamples < _holdUntil[channel])
            {
                _hold[channel] = peak;
                _holdUntil[channel] = sample + _holdSamples;
            }

            reading.Hold[channel] = _hold[channel];
            reading.Clipped |= peak >= 1.0f;

            if (channel < _truePeaks.Length)
            {
                TruePeak detector = _truePeaks[channel];
                foreach (float value in plane)
                {
                    reading.TruePeak = Math.Max(reading.TruePeak, detector.Push(value));
                }
            }
        }

        if (_loudness is { } loudness)
        {
            if (Interlocked.Exchange(ref _resetIntegrated, 0) == 1)
            {
                loudness.ResetIntegrated();
            }

            loudness.Process(buffer, offset, frames);
            reading.Momentary = loudness.Momentary;
            reading.ShortTerm = loudness.ShortTerm;
            reading.Integrated = loudness.Integrated;
        }

        Readings.Write(reading);
    }

    /// <summary>Drops the held peaks, for a seek.</summary>
    public void Reset()
    {
        Array.Clear(_hold);
        Array.Clear(_holdUntil);
        _loudness?.Reset();
        foreach (TruePeak detector in _truePeaks)
        {
            detector.Reset();
        }
    }
}

/// <summary>
/// A fixed ring of meter readings, one writer and one reader, with no lock.
/// </summary>
/// <remarks>
/// The audio thread writes about ninety readings a second and the UI reads thirty times a second,
/// so the reader usually wants only the newest. A reading is larger than a word, so each slot
/// carries a sequence number that is odd while it is being written: a reader that sees it change
/// under it knows the copy is torn and tries the next one. A reader that falls a whole ring
/// behind skips forward rather than reading what has been overwritten.
/// </remarks>
public sealed class MeterRing
{
    private const int Capacity = 64;
    private readonly Slot[] _slots = new Slot[Capacity];
    private long _written;
    private long _read;

    /// <summary>Readings written since the ring was made.</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>The oldest reading not yet read, or false when the reader is caught up.</summary>
    public bool TryRead(out MeterReading reading)
    {
        while (true)
        {
            long written = Volatile.Read(ref _written);

            if (_read >= written)
            {
                reading = default;
                return false;
            }

            if (written - _read > Capacity - 1)
            {
                _read = written - (Capacity - 1);
            }

            long index = _read++;
            if (TryCopy(index, out reading))
            {
                return true;
            }
        }
    }

    /// <summary>The newest reading, skipping everything older.</summary>
    public bool TryReadLatest(out MeterReading reading)
    {
        reading = default;
        bool any = false;

        while (TryRead(out MeterReading next))
        {
            reading = next;
            any = true;
        }

        return any;
    }

    /// <summary>Publishes a reading. One writer only: the audio thread.</summary>
    internal void Write(in MeterReading reading)
    {
        long index = _written;
        ref Slot slot = ref _slots[index % Capacity];

        Volatile.Write(ref slot.Sequence, (index * 2) + 1);
        Interlocked.MemoryBarrier();
        slot.Reading = reading;
        Volatile.Write(ref slot.Sequence, (index * 2) + 2);
        Volatile.Write(ref _written, index + 1);
    }

    private bool TryCopy(long index, out MeterReading reading)
    {
        ref Slot slot = ref _slots[index % Capacity];
        long expected = (index * 2) + 2;

        long before = Volatile.Read(ref slot.Sequence);
        reading = slot.Reading;
        Interlocked.MemoryBarrier();
        long after = Volatile.Read(ref slot.Sequence);

        return before == expected && after == expected;
    }

    private struct Slot
    {
        public long Sequence;
        public MeterReading Reading;
    }
}
