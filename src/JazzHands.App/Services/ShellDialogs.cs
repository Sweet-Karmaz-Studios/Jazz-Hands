using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using JazzHands.DialogHost;
using Serilog;

namespace JazzHands.App.Services;

/// <summary>
/// Shows Windows' file dialogs in <c>jazz-dialog.exe</c>, a process of their own, so an Explorer
/// add-on that crashes the dialog cannot take the editor with it.
/// </summary>
/// <remarks>
/// A file dialog loads other programs' Explorer add-ons into whatever process shows it. On
/// 2026-09-30 one built on .NET Framework 2.0 started that runtime inside the editor
/// and the process ended at once, with nothing saved and no report of our own. Now the editor waits
/// for the helper with its windows taking no input and still painting, as a dialog of its own
/// would leave them, reads the chosen paths from the helper's output, and says what happened when
/// the helper dies. Without the helper beside the editor (some test runs) the dialog is shown here.
/// </remarks>
internal static partial class ShellDialogs
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(ShellDialogs));

    /// <summary>Where the helper is: beside the editor, unless a test says otherwise.</summary>
    internal static string HelperPath => HelperOverride ?? Path.Combine(AppContext.BaseDirectory, "jazz-dialog.exe");

    /// <summary>For tests: a stand-in helper.</summary>
    internal static string? HelperOverride { get; set; }

    /// <summary>Says the dialog failed: a message box, or what a test puts here.</summary>
    internal static Action<Window?, string, string> Warn { get; set; } = (owner, message, title) =>
        MessageBox.Show(owner ?? Application.Current?.MainWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Shows a dialog; the paths chosen, or null when it was cancelled or failed.</summary>
    public static IReadOnlyList<string>? Pick(FilePick pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? Application.Current?.MainWindow;
        if (!File.Exists(HelperPath))
        {
            return pick.Show(owner);
        }

        IntPtr handle = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
        var start = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(pick.ToArgument());
        start.ArgumentList.Add(handle.ToInt64().ToString(CultureInfo.InvariantCulture));

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The file dialog helper did not start.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warning(exception, "The file dialog helper would not start; showing the dialog in the editor");
            return pick.Show(owner);
        }

        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            bool disabled = handle != IntPtr.Zero && !EnableWindow(handle, false);
            try
            {
                Wait(process);
            }
            finally
            {
                if (disabled)
                {
                    EnableWindow(handle, true);
                }

                owner?.Activate();
            }

            int code = process.ExitCode;
            if (code == FilePick.Chosen)
            {
                return FilePick.ReadPaths(output.GetAwaiter().GetResult());
            }

            if (code == FilePick.Cancelled)
            {
                return null;
            }

            Log.Warning("The file dialog helper ended with {Code:X8}: {Title}", code, pick.Title);
            Warn(
                owner,
                string.Create(CultureInfo.InvariantCulture, $"Windows' file dialog closed unexpectedly (0x{code:X8}). That is usually another program's Explorer add-on, which the dialog loads to draw its icons. Jazz Hands is fine and nothing was lost. Try again, or type the path instead."),
                pick.Title);
            return null;
        }
    }

    /// <summary>Keeps the editor's windows painting while the helper runs, as a modal dialog would.</summary>
    private static void Wait(Process process)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => dispatcher.BeginInvoke(() => frame.Continue = false);
        if (!process.HasExited)
        {
            Dispatcher.PushFrame(frame);
        }

        process.WaitForExit();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enable);
}
