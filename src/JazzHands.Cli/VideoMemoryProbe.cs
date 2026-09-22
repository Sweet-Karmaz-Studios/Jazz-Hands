using JazzHands.Render;

namespace JazzHands.Cli;

/// <summary>
/// Reports how much video memory this process holds, by asking the adapter through a throwaway
/// render device.
/// </summary>
/// <remarks>
/// Decoder surfaces live in VRAM, so working set barely moves when they leak. This is what makes
/// the S2 memory criterion answerable. Creating a device just to ask is cheap next to a decode
/// run, and the alternative is trusting a number that measures the wrong pool.
/// </remarks>
internal sealed class VideoMemoryProbe : IDisposable
{
    private readonly RenderDevice? _device;

    /// <summary>Creates the probe, falling back to reporting zero when there is no usable adapter.</summary>
    public VideoMemoryProbe()
    {
        try
        {
            RenderDevice device = RenderDevice.Create();
            _device = device.IsHardware ? device : null;
            if (_device is null)
            {
                device.Dispose();
            }
        }
        catch (RenderDeviceException)
        {
            _device = null;
        }
    }

    /// <summary>True when the adapter can report video memory.</summary>
    public bool IsAvailable => _device is not null;

    /// <summary>Bytes of video memory this process currently holds, or zero when unavailable.</summary>
    public long UsedBytes() => _device?.QueryVideoMemory().UsedBytes ?? 0;

    /// <inheritdoc />
    public void Dispose() => _device?.Dispose();
}
