using System.Runtime.InteropServices;

namespace JazzHands.App.Controls.Preview;

/// <summary>
/// A 1x1 hidden message-only window used as the focus window for the D3D9Ex device. D3D9 insists
/// on an HWND even when it never draws into one, and using WPF's own window here would let a
/// device reset interfere with the shell.
/// </summary>
internal static partial class FocusWindow
{
    private const int HwndMessage = -3;

    /// <summary>Creates the hidden window. Returns its handle.</summary>
    public static IntPtr Create()
    {
        IntPtr module = GetModuleHandle(null);
        IntPtr hwnd = CreateWindowEx(
            0,
            "STATIC",
            "JazzHandsD3D9Focus",
            0,
            0,
            0,
            1,
            1,
            new IntPtr(HwndMessage),
            IntPtr.Zero,
            module,
            IntPtr.Zero);

        return hwnd != IntPtr.Zero
            ? hwnd
            : throw new InvalidOperationException(
                $"Could not create the D3D9 focus window (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Destroys a window created by <see cref="Create"/>.</summary>
    public static void Destroy(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            DestroyWindow(hwnd);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? moduleName);
}
