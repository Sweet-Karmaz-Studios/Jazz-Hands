using JazzHands.Audio.Output;
using JazzHands.Engine.Caching;
using JazzHands.Engine.Playback;

namespace JazzHands.App.Services;

/// <summary>
/// The playback settings the engine applies while running: which speakers, scrub sound, proxies.
/// </summary>
/// <remarks>A seam so the Settings dialog can be tested without a sound card.</remarks>
public interface IPlaybackPreferences
{
    /// <summary>The playback devices Windows knows about.</summary>
    IReadOnlyList<AudioDeviceInfo> Devices();

    /// <summary>The chosen device, or null for the Windows default. Setting it moves the sound at once.</summary>
    string? Device { get; set; }

    /// <summary>Whether scrubbing plays grains of sound. Takes effect at once.</summary>
    bool ScrubAudio { get; set; }

    /// <summary>Whether the preview plays proxies.</summary>
    bool ProxiesEnabled { get; }
}

/// <summary>The engine's transport and proxy service.</summary>
/// <param name="transport">The transport, or null in a host without sound.</param>
/// <param name="proxies">The proxy service, or null.</param>
public sealed class EnginePlaybackPreferences(Transport? transport, ProxyService? proxies) : IPlaybackPreferences
{
    /// <inheritdoc />
    public IReadOnlyList<AudioDeviceInfo> Devices() => WasapiOutput.HasDevice() ? WasapiOutput.Devices() : [];

    /// <inheritdoc />
    public string? Device
    {
        get => (transport?.Output as WasapiOutput)?.DeviceId;
        set
        {
            if (transport?.Output is WasapiOutput output)
            {
                output.DeviceId = value;
            }
        }
    }

    /// <inheritdoc />
    public bool ScrubAudio
    {
        get => transport?.ScrubAudio ?? false;
        set => transport?.ScrubAudio = value;
    }

    /// <inheritdoc />
    public bool ProxiesEnabled => proxies?.Enabled ?? false;
}
