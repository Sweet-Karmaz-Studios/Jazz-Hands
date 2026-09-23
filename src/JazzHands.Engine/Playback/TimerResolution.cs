using System.Runtime.InteropServices;

namespace JazzHands.Engine.Playback;

/// <summary>
/// Raises the system timer resolution to 1 ms while held.
/// </summary>
/// <remarks>
/// A wait otherwise rounds up to the 15.6 ms scheduler tick, which turns a 60 Hz presentation
/// loop into one that presents every other tick at best and looks exactly like dropped frames.
/// The composition thread holds one only while playing, because a raised resolution costs power
/// for the whole machine.
/// </remarks>
internal sealed partial class TimerResolution : IDisposable
{
    private readonly uint _period;
    private bool _disposed;

    /// <summary>Requests the given period in milliseconds.</summary>
    public TimerResolution(uint milliseconds = 1)
    {
        _period = milliseconds;
        _ = TimeBeginPeriod(_period);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = TimeEndPeriod(_period);
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);
}
