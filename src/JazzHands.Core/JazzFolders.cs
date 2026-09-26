namespace JazzHands.Core;

/// <summary>
/// Where Jazz Hands keeps what is the person's rather than a project's: settings, presets, the
/// keymap and layouts in the roaming folder; caches, logs, the queue and instances in the local.
/// </summary>
/// <remarks>
/// <c>JAZZ_HOME</c> moves both under one folder for a process (Phase 33): the UI automation suite
/// and anything else that runs the real editor without touching the person's own settings,
/// recent projects, window layout or Windows registration.
/// </remarks>
public static class JazzFolders
{
    /// <summary>The variable that moves both folders.</summary>
    public const string HomeVariable = "JAZZ_HOME";

    /// <summary>The folder <see cref="HomeVariable"/> names, or null when it is not set.</summary>
    public static string? Home =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home ? Path.GetFullPath(home) : null;

    /// <summary>True when this process runs apart from the person's own folders.</summary>
    public static bool IsIsolated => Home is not null;

    /// <summary>%APPDATA%\JazzHands, or JAZZ_HOME\roaming.</summary>
    public static string Roaming => Home is { } home
        ? Path.Combine(home, "roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JazzHands");

    /// <summary>%LOCALAPPDATA%\JazzHands, or JAZZ_HOME\local.</summary>
    public static string Local => Home is { } home
        ? Path.Combine(home, "local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JazzHands");
}
