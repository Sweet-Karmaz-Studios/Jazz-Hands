using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using JazzHands.App.Services;
using JazzHands.Control;
using Serilog;

namespace JazzHands.App.Shell;

/// <summary>
/// What the editor does as it starts: which project to open, and whether another editor is
/// already running and should be handed the job instead.
/// </summary>
/// <remarks>
/// <para>
/// A project named on the command line (a double-clicked <c>.jazz</c>) is opened; with none, the
/// last project when Settings says so and it is still there; otherwise an empty one.
/// </para>
/// <para>
/// One editor at a time: a second launch finds the first in the instance list, asks it over the
/// control pipe to open the project (<c>app.open</c>) or just to come to the front
/// (<c>app.activate</c>), and exits. <c>--new-instance</c> starts a second editor on purpose. If
/// the first does not answer in a few seconds, the second starts as usual rather than leaving
/// the person with nothing.
/// </para>
/// </remarks>
public static partial class Startup
{
    /// <summary>The argument that starts another editor even when one is running.</summary>
    public const string NewInstance = "--new-instance";

    /// <summary>The project to open, or null for an empty one.</summary>
    public static string? ProjectToOpen(IReadOnlyList<string> args, EditorSettings editor, RecentProjects recent, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(recent);
        exists ??= File.Exists;

        if (args.FirstOrDefault(argument => argument.EndsWith(".jazz", StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return Path.GetFullPath(named);
        }

        return editor.OpenLastProject && recent.Paths.FirstOrDefault() is { } last && exists(last) ? last : null;
    }

    /// <summary>
    /// Hands a launch to an editor already running: true when one took it and this process should exit.
    /// </summary>
    /// <param name="project">A project to open there, or null to bring it to the front.</param>
    /// <param name="registryPath">The instance list, or null for the per-user one.</param>
    /// <param name="timeout">How long to wait for it.</param>
    /// <param name="self">This process, which is never its own editor; for tests.</param>
    public static async Task<bool> TryHandOffAsync(string? project, string? registryPath = null, TimeSpan? timeout = null, int? self = null)
    {
        int me = self ?? Environment.ProcessId;
        InstanceInfo? running = InstanceRegistry.List(registryPath)
            .FirstOrDefault(instance => instance.Kind == "gui" && instance.Pid != me && instance.Pipe is not null);
        if (running is null)
        {
            return false;
        }

        try
        {
            // Let the running editor put its window in front of whatever this launch came from.
            _ = AllowSetForegroundWindow(running.Pid);
            TimeSpan wait = timeout ?? TimeSpan.FromSeconds(3);
            await using JazzClient client = await JazzClient.ConnectAsync($"pipe:{running.Pipe}", "launcher", wait, registryPath).ConfigureAwait(false);
            using var cancel = new CancellationTokenSource(wait);
            if (project is null)
            {
                await client.CallAsync("app.activate", null, cancel.Token).ConfigureAwait(false);
            }
            else
            {
                await client.CallAsync("app.open", new JsonObject { ["path"] = Path.GetFullPath(project) }, cancel.Token).ConfigureAwait(false);
            }

            Log.ForContext(typeof(Startup)).Information("Handed the launch to the editor running as {Pid}", running.Pid);
            return true;
        }
        catch (Exception error) when (error is AttachException or JsonRpcException or OperationCanceledException or IOException)
        {
            Log.ForContext(typeof(Startup)).Warning(error, "The editor running as {Pid} did not answer; starting another", running.Pid);
            return false;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}

/// <summary>
/// The methods the editor answers over the control pipe itself (<c>app.open</c>, <c>app.activate</c>),
/// filled in by the application once its window exists.
/// </summary>
public sealed class AppHostMethods
{
    /// <summary>The methods, by name, as the control server's target takes them.</summary>
    public Dictionary<string, Func<JsonObject, Task<JsonNode?>>> Methods { get; } = new(StringComparer.Ordinal);
}
