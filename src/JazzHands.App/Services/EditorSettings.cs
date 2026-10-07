using System.IO;
using JazzHands.Engine.Settings;

namespace JazzHands.App.Services;

/// <summary>
/// The editor's own preferences: the <c>editor</c> section of <c>settings.json</c>.
/// </summary>
/// <remarks>
/// What belongs to a project (frame rate, tracks, presets saved with it) is in the project. What
/// belongs to the person and this machine is here: which speakers, which GPU, where exports go.
/// The cache has its section and the control server its own.
/// </remarks>
public sealed record EditorSettings
{
    /// <summary>The playback device's id, or null for the Windows default.</summary>
    public string? AudioDevice { get; init; }

    /// <summary>Play short grains of sound while scrubbing.</summary>
    public bool ScrubAudio { get; init; } = true;

    /// <summary>Draw waveforms on a decibel scale, -60 dB at the middle line, so quiet speech shows; off draws them linear.</summary>
    public bool WaveformsInDecibels { get; init; }

    /// <summary>The preview's resolution: auto, full, half or quarter.</summary>
    public string PreviewQuality { get; init; } = "auto";

    /// <summary>Which adapter renders: auto, warp or an adapter number. From the next start.</summary>
    public string Gpu { get; init; } = "auto";

    /// <summary>The preset the export dialog opens on.</summary>
    public string ExportPreset { get; init; } = "youtube-1080p";

    /// <summary>The folder the export dialog suggests, or null for beside the project.</summary>
    public string? ExportFolder { get; init; }

    /// <summary>Open the last project when the editor starts with none named.</summary>
    public bool OpenLastProject { get; init; }

    /// <summary>The workspace the editor opens in: the one it was left in.</summary>
    public string Workspace { get; init; } = "Edit";

    /// <summary>Where the window was, in device independent pixels, or null the first time.</summary>
    public WindowPlacement? WindowBounds { get; init; }

    /// <summary>Whether the window was maximised.</summary>
    public bool WindowMaximized { get; init; }

    /// <summary>Closing the window keeps Jazz Hands running in the notification area; false quits.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>The one notification that closing hid the window rather than quitting has been shown.</summary>
    public bool TrayNoticeShown { get; init; }

    /// <summary>Start hidden in the notification area when Windows starts.</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>Windows notifications for finished and failed exports, proxies and recovery.</summary>
    public bool WindowsNotifications { get; init; } = true;

    /// <summary>A Windows notification when Claude Code or another client attaches.</summary>
    public bool NotifyClientAttached { get; init; }

    /// <summary>Puts hand-edited values back in range.</summary>
    public static EditorSettings Tidy(EditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            PreviewQuality = settings.PreviewQuality is "auto" or "full" or "half" or "quarter" ? settings.PreviewQuality : "auto",
            Gpu = JazzHands.Render.GpuChoice.TryParse(settings.Gpu, out _) ? settings.Gpu : "auto",
            ExportPreset = string.IsNullOrWhiteSpace(settings.ExportPreset) ? "youtube-1080p" : settings.ExportPreset,
            Workspace = string.IsNullOrWhiteSpace(settings.Workspace) ? "Edit" : settings.Workspace,
        };
    }

    /// <summary>The store over the per-user file, or another for tests.</summary>
    public static SettingsSection<EditorSettings> Store(string? path = null) => new("editor", path, Tidy);
}

/// <summary>The projects opened lately, newest first: the <c>recent</c> section of <c>settings.json</c>.</summary>
public sealed record RecentProjects
{
    /// <summary>How many are kept.</summary>
    public const int Limit = 10;

    /// <summary>Full paths, newest first.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>The list with a project put at the top, without duplicates, cut to <see cref="Limit"/>.</summary>
    public RecentProjects With(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        return this with { Paths = [full, .. Paths.Where(item => !string.Equals(item, full, StringComparison.OrdinalIgnoreCase)).Take(Limit - 1)] };
    }

    /// <summary>The list without a project, for one that is gone.</summary>
    public RecentProjects Without(string path) =>
        this with { Paths = [.. Paths.Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase))] };

    /// <summary>The store over the per-user file, or another for tests.</summary>
    public static SettingsSection<RecentProjects> Store(string? path = null) =>
        new("recent", path, recent => recent with { Paths = [.. recent.Paths.Where(item => !string.IsNullOrWhiteSpace(item)).Take(Limit)] });
}

/// <summary>Where the window was.</summary>
/// <param name="Left">Its left edge.</param>
/// <param name="Top">Its top edge.</param>
/// <param name="Width">Its width.</param>
/// <param name="Height">Its height.</param>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height)
{
    /// <summary>As a rectangle.</summary>
    public System.Windows.Rect ToRect() => new(Left, Top, Math.Max(1, Width), Math.Max(1, Height));
}
