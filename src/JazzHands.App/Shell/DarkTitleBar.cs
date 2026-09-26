using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JazzHands.App.Shell;

/// <summary>
/// Dark window frames: the title bar of every window the editor opens (the main window, dialogs,
/// floating panels) drawn dark by Windows, to match the theme. Without it each window had the
/// light Windows title bar over the dark editor.
/// </summary>
public static partial class DarkTitleBar
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE: Windows 11, and Windows 10 from 20H1.
    private const int ImmersiveDarkMode = 20;
    private static bool _registered;

    /// <summary>Makes every window from now on dark as it loads. Once per process, on the UI thread.</summary>
    public static void ForEveryWindow()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window)
            {
                Apply(window);
            }
        }));
    }

    /// <summary>Asks Windows to draw this window's frame dark. Harmless where it cannot.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            int on = 1;
            _ = DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref on, sizeof(int));
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
