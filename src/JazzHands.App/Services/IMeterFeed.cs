using JazzHands.Audio;

namespace JazzHands.App.Services;

/// <summary>
/// Where the meters panel gets its readings.
/// </summary>
/// <remarks>
/// An interface so the panel can be built and drawn without a sound card, the same way
/// <see cref="ISession"/> lets the media panel be built without an engine.
/// </remarks>
public interface IMeterFeed
{
    /// <summary>How many channels the mix has, which is how many bars to draw.</summary>
    int Channels { get; }

    /// <summary>The newest reading since the last call, or false when nothing has been mixed.</summary>
    /// <remarks>One reader only: the ring behind it is single consumer.</remarks>
    bool TryReadLatest(out MeterReading reading);
}

/// <summary>The transport's master meter, through the tap the Mixer shares.</summary>
public sealed class TransportMeterFeed : IMeterFeed
{
    private readonly TransportMeters _meters;
    private readonly MeterTap.Reader _reader;

    /// <summary>Opens a reader on the master.</summary>
    /// <param name="meters">The transport's meters. Owned by the host, not by this adapter.</param>
    public TransportMeterFeed(TransportMeters meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _meters = meters;
        _reader = meters.Master.Open();
    }

    /// <inheritdoc />
    public int Channels => _meters.Channels;

    /// <inheritdoc />
    public bool TryReadLatest(out MeterReading reading) => _reader.TryRead(out reading);
}
