using System.Runtime.InteropServices;
using System.Windows;

namespace JazzHands.App.Controls.Preview;

/// <summary>The monitors attached to the desktop, in device pixels.</summary>
/// <remarks>
/// WPF has no per-monitor API of its own; <c>SystemParameters</c> only describes the primary
/// display and the virtual desktop. The full screen preview needs to know which monitor the
/// editor is on so it can go to a different one.
/// </remarks>
internal static partial class Monitors
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Every monitor's bounds, primary first.</summary>
    public static IReadOnlyList<Int32Rect> All()
    {
        var found = new List<(Int32Rect Bounds, bool Primary)>();

        MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref Native.Rect clip, IntPtr data) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                found.Add((ToRect(info.Monitor), (info.Flags & 1) != 0));
            }

            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return [.. found.OrderByDescending(monitor => monitor.Primary).Select(monitor => monitor.Bounds)];
    }

    /// <summary>The bounds of the monitor a window is mostly on.</summary>
    public static Int32Rect Of(IntPtr window)
    {
        IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) ? ToRect(info.Monitor) : Int32Rect.Empty;
    }

    /// <summary>A monitor other than the one a window is on, or that one when it is the only one.</summary>
    public static Int32Rect OtherThan(IntPtr window)
    {
        Int32Rect current = Of(window);
        IReadOnlyList<Int32Rect> all = All();
        return all.FirstOrDefault(bounds => bounds != current, current);
    }

    private static Int32Rect ToRect(Native.Rect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Native.Rect clip, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref Native.MonitorInfo info);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }
    }
}
