namespace JazzHands.Audio;

/// <summary>
/// Planar float samples, one array per channel, allocated once and reused.
/// </summary>
/// <remarks>
/// Every stage of the graph reads and writes these rather than spans of its own, because a span
/// cannot be an array element and a graph needs "one plane per channel" as a value it can pass
/// around. The arrays are made in the constructor and never again, which is what keeps the
/// audio thread free of allocation.
/// </remarks>
public sealed class AudioBuffer
{
    private readonly float[][] _planes;

    /// <summary>Creates a buffer.</summary>
    /// <param name="channels">How many planes.</param>
    /// <param name="capacity">How many samples each plane holds.</param>
    public AudioBuffer(int channels, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Channels = channels;
        Capacity = capacity;
        _planes = new float[channels][];

        for (int channel = 0; channel < channels; channel++)
        {
            _planes[channel] = new float[capacity];
        }
    }

    /// <summary>How many planes there are.</summary>
    public int Channels { get; }

    /// <summary>How many samples each plane holds.</summary>
    public int Capacity { get; }

    /// <summary>One channel's samples, all of them.</summary>
    public Span<float> Plane(int channel) => _planes[channel];

    /// <summary>One channel's samples over a range.</summary>
    public Span<float> Plane(int channel, int offset, int frames) => _planes[channel].AsSpan(offset, frames);

    /// <summary>Silences a range of every plane.</summary>
    public void Clear(int offset, int frames)
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            _planes[channel].AsSpan(offset, frames).Clear();
        }
    }

    /// <summary>Silences everything.</summary>
    public void Clear() => Clear(0, Capacity);
}
