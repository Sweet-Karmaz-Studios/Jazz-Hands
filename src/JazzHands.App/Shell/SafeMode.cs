using System.IO;

namespace JazzHands.App.Shell;

/// <summary>
/// <c>--safe-mode</c>: the editor started with nothing of its own that could be what is crashing
/// it (Phase 33). Pictures are drawn on WARP and decoded on the CPU, the window has the built-in
/// layout rather than a saved one, the cache is a fresh empty folder for this run only, and custom
/// transitions are not loaded. The project and the settings are the person's own; nothing they
/// have is changed, so leaving safe mode is starting without the switch.
/// </summary>
public static class SafeMode
{
    /// <summary>The switch.</summary>
    public const string Switch = "--safe-mode";

    /// <summary>True when this process was started with the switch.</summary>
    public static bool IsOn { get; private set; }

    /// <summary>The empty folder this run uses for layouts and the cache, or null outside safe mode.</summary>
    public static string? Folder { get; private set; }

    /// <summary>Turns safe mode on when <paramref name="args"/> ask for it; call before anything reads the GPU choice or the cache.</summary>
    public static bool Apply(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (IsOn || !args.Contains(Switch, StringComparer.OrdinalIgnoreCase))
        {
            return IsOn;
        }

        IsOn = true;
        Folder = Path.Combine(Path.GetTempPath(), $"jazz-safe-mode-{Environment.ProcessId}");
        Directory.CreateDirectory(Path.Combine(Folder, "cache"));
        Directory.CreateDirectory(Path.Combine(Folder, "layouts"));

        Render.RenderDevice.Preferred = new Render.GpuChoice(Warp: true);
        Environment.SetEnvironmentVariable(Media.Import.CacheManager.FolderVariable, Path.Combine(Folder, "cache"));
        return true;
    }

    /// <summary>Removes this run's folder; called on the way out.</summary>
    public static void Clean()
    {
        if (Folder is null)
        {
            return;
        }

        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.ForContext(typeof(SafeMode)).Warning(error, "Could not remove the safe mode folder {Folder}", Folder);
        }
    }
}
