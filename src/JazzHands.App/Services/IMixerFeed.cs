using System.Collections.Concurrent;
using JazzHands.Audio;
using JazzHands.Engine.Playback;

namespace JazzHands.App.Services;

/// <summary>
/// Where the Mixer panel gets its readings: the master's and each track's.
/// </summary>
/// <remarks>
/// An interface so the mixer can be built, ticked and drawn without a sound card, as
/// <see cref="IMeterFeed"/> does for the Meters panel.
/// </remarks>
public interface IMixerFeed
{
    /// <summary>How many channels the mix has.</summary>
    int Channels { get; }

    /// <summary>The master's reading since the last call, or false when nothing was mixed.</summary>
    bool TryReadMaster(out MeterReading reading);

    /// <summary>A track's reading since the last call for it, after its volume and pan.</summary>
    bool TryReadTrack(string trackId, out MeterReading reading);

    /// <summary>Starts the integrated loudness again from now.</summary>
    void ResetLoudness();
}

/// <summary>
/// The transport's meters, tapped so that the Meters panel and the Mixer can both read the master.
/// </summary>
/// <param name="transport">The transport. Owned by the host.</param>
public sealed class TransportMeters(Transport transport)
{
    private readonly ConcurrentDictionary<string, MeterTap> _tracks = new(StringComparer.Ordinal);

    /// <summary>How many channels the output plays.</summary>
    public int Channels => transport.Output.Channels;

    /// <summary>The master's meter.</summary>
    public MeterTap Master { get; } = new(() => transport.Meters);

    /// <summary>A track's meter, after its volume and pan.</summary>
    public MeterTap Track(string trackId) =>
        _tracks.GetOrAdd(trackId, id => new MeterTap(() => transport.Graph.TrackMeter(id)));

    /// <summary>Starts the master's integrated loudness again.</summary>
    public void ResetLoudness() => transport.Graph.Meter.ResetIntegrated();
}

/// <summary>The transport's meters, as the Mixer reads them.</summary>
public sealed class TransportMixerFeed : IMixerFeed
{
    private readonly TransportMeters _meters;
    private readonly MeterTap.Reader _master;
    private readonly Dictionary<string, MeterTap.Reader> _tracks = new(StringComparer.Ordinal);

    /// <summary>Opens a reader on the master.</summary>
    public TransportMixerFeed(TransportMeters meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _meters = meters;
        _master = meters.Master.Open();
    }

    /// <inheritdoc />
    public int Channels => _meters.Channels;

    /// <inheritdoc />
    public bool TryReadMaster(out MeterReading reading) => _master.TryRead(out reading);

    /// <inheritdoc />
    public bool TryReadTrack(string trackId, out MeterReading reading)
    {
        if (!_tracks.TryGetValue(trackId, out MeterTap.Reader? reader))
        {
            reader = _meters.Track(trackId).Open();
            _tracks[trackId] = reader;
        }

        return reader.TryRead(out reading);
    }

    /// <inheritdoc />
    public void ResetLoudness() => _meters.ResetLoudness();
}
