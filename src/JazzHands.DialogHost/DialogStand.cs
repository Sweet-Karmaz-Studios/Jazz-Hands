using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JazzHands.DialogHost;

/// <summary>
/// An invisible window owned by the editor's, over the middle of it, for the file dialog to belong
/// to: so it opens over the editor and stays in front of it.
/// </summary>
/// <remarks>
/// It cannot stay minimized. Minimized it takes the dialog with it, and as it has no button on the
/// taskbar and nothing to see, nobody can bring either back while the editor waits for an answer
/// (seen on screen, 2026-10-09). So it has no minimize box, and anything that minimizes it anyway
/// is undone at once, which shows the dialog again.
/// </remarks>
public static partial class DialogStand
{
    /// <summary>Makes and shows the stand over a window, given by its handle; zero for none.</summary>
    public static Window Create(IntPtr editor)
    {
        var stand = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1,
            Height = 1,
            Opacity = 0,
            AllowsTransparency = true,
        };

        if (editor != IntPtr.Zero && GetWindowRect(editor, out Rect32 rect))
        {
            // Pixels, and the stand's position is in device independent units: near enough for a
            // dialog that centres itself on its owner anyway.
            stand.WindowStartupLocation = WindowStartupLocation.Manual;
            stand.Left = (rect.Left + rect.Right) / 2.0;
            stand.Top = (rect.Top + rect.Bottom) / 2.0;
        }

        stand.StateChanged += (_, _) =>
        {
            if (stand.WindowState == WindowState.Minimized)
            {
                stand.WindowState = WindowState.Normal;
            }
        };

        var interop = new WindowInteropHelper(stand) { Owner = editor };
        interop.EnsureHandle();
        stand.Show();
        return stand;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out Rect32 rect);
}
