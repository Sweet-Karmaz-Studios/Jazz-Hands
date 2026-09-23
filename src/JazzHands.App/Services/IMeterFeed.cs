using JazzHands.Audio;
using JazzHands.Engine.Playback;

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

/// <summary>The transport's master meter.</summary>
/// <param name="transport">The transport. Owned by the host, not by this adapter.</param>
public sealed class TransportMeterFeed(Transport transport) : IMeterFeed
{
    /// <inheritdoc />
    public int Channels => transport.Output.Channels;

    /// <inheritdoc />
    public bool TryReadLatest(out MeterReading reading) => transport.Meters.TryReadLatest(out reading);
}
