using JazzHands.Audio;

namespace JazzHands.Engine.Playback;

/// <summary>
/// Stretched audio on its way from the decode thread to the audio thread.
/// </summary>
/// <remarks>
/// Shuttle audio at a rate other than 1 goes through a tempo filter, and a filter built on
/// libavfilter allocates, so it cannot run on the audio thread. The decode thread runs it instead
/// and writes the result here, keeping about half a second queued; the audio thread reads from
/// here instead of mixing. One writer, one reader, and the two positions are the only shared
/// state, so neither side ever waits.
///
/// A ring belongs to one transport request. A new rate or a seek makes a new ring rather than
/// emptying this one, because emptying would mean the writer touching the reader's position.
/// </remarks>
internal sealed class StretchRing
{
    private readonly float[][] _planes;
    private readonly int _capacity;
    private long _written;
    private long _read;

    /// <summary>Creates an empty ring for a request.</summary>
    /// <param name="sequence">The transport request this ring's audio belongs to.</param>
    /// <param name="channels">How many channels.</param>
    /// <param name="capacity">How many samples per channel it holds.</param>
    public StretchRing(long sequence, int channels, int capacity)
    {
        Sequence = sequence;
        _capacity = capacity;
        _planes = new float[channels][];

        for (int channel = 0; channel < channels; channel++)
        {
            _planes[channel] = new float[capacity];
        }
    }

    /// <summary>The request this ring was filled for.</summary>
    public long Sequence { get; }

    /// <summary>Samples waiting to be read.</summary>
    public int Available => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    /// <summary>Room left for the writer.</summary>
    public int Space => _capacity - Available;

    /// <summary>Samples the reader wanted and did not find, which is a gap in what was heard.</summary>
    public long Shortfall { get; private set; }

    /// <summary>Appends samples. Writer only.</summary>
    public void Write(float[][] planes, int count)
    {
        count = Math.Min(count, Space);
        long written = _written;

        for (int channel = 0; channel < _planes.Length; channel++)
        {
            float[] target = _planes[channel];
            float[] source = channel < planes.Length ? planes[channel] : planes[^1];

            for (int index = 0; index < count; index++)
            {
                target[(written + index) % _capacity] = source[index];
            }
        }

        Volatile.Write(ref _written, written + count);
    }

    /// <summary>Takes samples into a buffer. Reader only; no allocation, no lock.</summary>
    /// <returns>How many samples per channel it had.</returns>
    public int Read(AudioBuffer output, int offset, int frames)
    {
        long read = _read;
        int count = Math.Min(frames, (int)(Volatile.Read(ref _written) - read));

        for (int channel = 0; channel < output.Channels; channel++)
        {
            Span<float> target = output.Plane(channel, offset, count);
            float[] source = _planes[Math.Min(channel, _planes.Length - 1)];

            for (int index = 0; index < count; index++)
            {
                target[index] = source[(read + index) % _capacity];
            }
        }

        Volatile.Write(ref _read, read + count);
        Shortfall += frames - count;
        return count;
    }
}
