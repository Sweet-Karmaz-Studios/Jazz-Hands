using System.Windows;

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
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || FilePick.FromArgument(args[0]) is not { } pick)
        {
            return FilePick.BadRequest;
        }

        IntPtr editor = args.Length > 1 && long.TryParse(args[1], out long handle) ? new IntPtr(handle) : IntPtr.Zero;
        Window? owner = editor == IntPtr.Zero ? null : DialogStand.Create(editor);
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
}
