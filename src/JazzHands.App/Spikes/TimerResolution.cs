using System.Runtime.InteropServices;

namespace JazzHands.App.Spikes;

/// <summary>
/// Raises the system timer resolution to 1 ms for the lifetime of the object. Thread.Sleep
/// otherwise rounds up to the 15.6 ms scheduler tick, which makes a 60 Hz sleep loop land at
/// about 53 Hz and looks exactly like a rendering problem when it is not.
/// </summary>
internal sealed partial class TimerResolution : IDisposable
{
    private readonly uint _period;
    private bool _disposed;

    /// <summary>Requests the given period in milliseconds.</summary>
    public TimerResolution(uint milliseconds = 1)
    {
        _period = milliseconds;
        TimeBeginPeriod(_period);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TimeEndPeriod(_period);
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);
}
