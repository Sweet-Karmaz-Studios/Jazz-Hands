using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JazzHands.App.Spikes;

/// <summary>The numbers Docs/SPIKES.md records for S1.</summary>
public sealed record SpikeReport
{
    /// <summary>Which presentation path was measured.</summary>
    public required string Mode { get; init; }

    /// <summary>The back buffer size.</summary>
    public required string Size { get; init; }

    /// <summary>Wall clock duration of the measured window, in seconds.</summary>
    public required double Seconds { get; init; }

    /// <summary>Frames the render thread completed.</summary>
    public required long RenderedFrames { get; init; }

    /// <summary>Frames handed to WPF.</summary>
    public required long PresentedFrames { get; init; }

    /// <summary>Presented frames per second: the number the exit criterion is about.</summary>
    public required double PresentedFps { get; init; }

    /// <summary>Frames the render thread produced that were never presented.</summary>
    public required long DroppedFrames { get; init; }

    /// <summary>Mean time the render thread spent drawing one frame, in milliseconds.</summary>
    public required double MeanRenderMs { get; init; }

    /// <summary>99th percentile render time, in milliseconds.</summary>
    public required double P99RenderMs { get; init; }

    /// <summary>Mean time the render thread waited for the GPU to finish, in milliseconds.</summary>
    public required double MeanGpuWaitMs { get; init; }

    /// <summary>Mean time the UI thread spent inside Lock/AddDirtyRect/Unlock, in milliseconds.</summary>
    public required double MeanPresentMs { get; init; }

    /// <summary>99th percentile present time, in milliseconds.</summary>
    public required double P99PresentMs { get; init; }

    /// <summary>Mean time acquiring the D3DImage lock, in milliseconds.</summary>
    public required double MeanLockMs { get; init; }

    /// <summary>Median lock acquisition, in milliseconds.</summary>
    public required double P50LockMs { get; init; }

    /// <summary>90th percentile lock acquisition, in milliseconds.</summary>
    public required double P90LockMs { get; init; }

    /// <summary>99th percentile lock acquisition, in milliseconds.</summary>
    public required double P99LockMs { get; init; }

    /// <summary>Mean time inside Unlock, where WPF takes the frame, in milliseconds.</summary>
    public required double MeanUnlockMs { get; init; }

    /// <summary>99th percentile Unlock, in milliseconds.</summary>
    public required double P99UnlockMs { get; init; }

    /// <summary>UI thread CPU as a percentage of one core over the measured window.</summary>
    public required double UiThreadCpuPercent { get; init; }

    /// <summary>Whole-process CPU as a percentage of one core over the measured window.</summary>
    public required double ProcessCpuPercent { get; init; }

    /// <summary>Managed bytes allocated per presented frame in steady state.</summary>
    public required double BytesAllocatedPerFrame { get; init; }

    /// <summary>Garbage collections during the measured window, by generation.</summary>
    public required IReadOnlyList<int> Collections { get; init; }

    /// <summary>Peak working set during the run, in megabytes.</summary>
    public required double PeakWorkingSetMb { get; init; }

    /// <summary>How many times the WPF front buffer was lost and rebuilt.</summary>
    public required int FrontBufferLossEvents { get; init; }

    /// <summary>How long the last rebuild after a front buffer loss took, in milliseconds.</summary>
    public required double LastRecoveryMs { get; init; }

    /// <summary>Resize events handled during the run.</summary>
    public required int ResizeEvents { get; init; }

    /// <summary>The DPI scales the window was shown at.</summary>
    public required IReadOnlyList<double> DpiScales { get; init; }

    /// <summary>Anything the run wants the reader to know.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Serializes the report the way SPIKES.md quotes it.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ReportJson.Options);
}

internal static class ReportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Collects per-frame timings without allocating while it does so.</summary>
internal sealed class Sampler(int capacity)
{
    private readonly double[] _samples = new double[capacity];
    private int _count;

    public int Count => _count;

    public void Add(double value)
    {
        if (_count < _samples.Length)
        {
            _samples[_count++] = value;
        }
    }

    public double Mean()
    {
        if (_count == 0)
        {
            return 0;
        }

        double total = 0;
        for (int i = 0; i < _count; i++)
        {
            total += _samples[i];
        }

        return total / _count;
    }

    public double Percentile(double percentile)
    {
        if (_count == 0)
        {
            return 0;
        }

        double[] sorted = _samples[.._count];
        Array.Sort(sorted);
        int index = (int)Math.Clamp(Math.Ceiling(percentile / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

/// <summary>
/// Reads kernel plus user time for a specific thread, which is the only honest way to answer
/// "how much CPU is the UI thread using" without a profiler attached.
/// </summary>
internal static partial class ThreadCpu
{
    private const int ThreadQueryInformation = 0x0040;

    /// <summary>Total CPU time the calling thread has used.</summary>
    public static TimeSpan ForCurrentThread()
    {
        IntPtr handle = OpenThread(ThreadQueryInformation, false, GetCurrentThreadId());
        if (handle == IntPtr.Zero)
        {
            return TimeSpan.Zero;
        }

        try
        {
            if (!GetThreadTimes(handle, out _, out _, out long kernel, out long user))
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromTicks(kernel + user);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Total CPU time this process has used, across every thread.</summary>
    public static TimeSpan ForProcess() => Process.GetCurrentProcess().TotalProcessorTime;

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenThread(int desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
