using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JazzHands.DialogHost;

/// <summary>
/// <c>jazz-dialog.exe</c>: shows one Windows file dialog for the editor and writes what was chosen
/// to standard output, as a JSON array of paths.
/// </summary>
/// <remarks>
/// A file dialog loads other programs' Explorer add-ons into its process to draw their icons, and
/// one of them can crash it: one built on .NET Framework 2.0 started that runtime
/// inside the editor and ended it at once (2026-09-30). Here only this process ends; the editor
/// sees the exit code and carries on. Arguments: the request (<see cref="FilePick.ToArgument"/>),
/// and the editor window to sit over, as a handle.
/// </remarks>
internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || FilePick.FromArgument(args[0]) is not { } pick)
        {
            return FilePick.BadRequest;
        }

        IntPtr editor = args.Length > 1 && long.TryParse(args[1], out long handle) ? new IntPtr(handle) : IntPtr.Zero;
        Window? owner = editor == IntPtr.Zero ? null : Stand(editor);
        IReadOnlyList<string>? chosen = pick.Show(owner);
        owner?.Close();

        if (chosen is null)
        {
            return FilePick.Cancelled;
        }

        Console.Out.Write(FilePick.WritePaths(chosen));
        Console.Out.Flush();
        return FilePick.Chosen;
    }

    /// <summary>
    /// An invisible window owned by the editor's, over the middle of it, for the dialog to belong
    /// to: so it opens over the editor and stays in front of it.
    /// </summary>
    private static Window Stand(IntPtr editor)
    {
        var stand = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1,
            Height = 1,
            Opacity = 0,
            AllowsTransparency = true,
        };

        if (GetWindowRect(editor, out Rect32 rect))
        {
            // Pixels, and the stand's position is in device independent units: near enough for a
            // dialog that centres itself on its owner anyway.
            stand.WindowStartupLocation = WindowStartupLocation.Manual;
            stand.Left = (rect.Left + rect.Right) / 2.0;
            stand.Top = (rect.Top + rect.Bottom) / 2.0;
        }

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
