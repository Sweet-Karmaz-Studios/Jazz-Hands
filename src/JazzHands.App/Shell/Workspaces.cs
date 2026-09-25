using System.Windows;

namespace JazzHands.App.Shell;

/// <summary>A built-in workspace: which panels show, and which are in front of their panes.</summary>
/// <param name="Name">What the Window menu calls it.</param>
/// <param name="Shown">The panels that show; the rest are hidden, one click away in the Window menu.</param>
/// <param name="Front">The panels put in front of the panes they share.</param>
public sealed record WorkspaceDefinition(string Name, IReadOnlyList<string> Shown, IReadOnlyList<string> Front)
{
    /// <summary>Edit, Color, Audio, Trim and Export, as Jazz Hands ships them.</summary>
    public static IReadOnlyList<WorkspaceDefinition> BuiltIn { get; } =
    [
        new("Edit",
            ["media", "effects", "subtitles", "meters", "exportQueue", "console", "history", "markers", "curves", "log", "inspector", "preview"],
            ["media", "inspector", "preview"]),
        new("Color",
            ["color", "scopes", "media", "inspector", "preview"],
            ["color", "inspector", "preview"]),
        new("Audio",
            ["mixer", "meters", "media", "inspector", "preview", "markers"],
            ["mixer", "inspector", "preview"]),
        new("Trim",
            ["media", "markers", "history", "preview"],
            ["media", "preview"]),
        new("Export",
            ["exportQueue", "log", "media", "preview"],
            ["exportQueue", "preview"]),
    ];

    /// <summary>The built-in workspace of a name, or null.</summary>
    public static WorkspaceDefinition? Find(string name) =>
        BuiltIn.FirstOrDefault(workspace => string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The window's workspaces as the menu sees them: which is in use, which exist, and switching,
/// saving and resetting. The window's layout service is one; tests use a fake.
/// </summary>
public interface IWorkspaces
{
    /// <summary>Raised when the workspace in use or the list changes.</summary>
    event EventHandler? Changed;

    /// <summary>The workspace in use.</summary>
    string Current { get; }

    /// <summary>Every workspace: the built-in ones, then a person's own.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>Shows a workspace without keeping the one before: as the editor starts.</summary>
    void Open(string name);

    /// <summary>Keeps the current layout under its name, and shows another.</summary>
    void Switch(string name);

    /// <summary>Keeps the current layout under a new name, which becomes current.</summary>
    void SaveAs(string name);

    /// <summary>Puts the current workspace back as it ships (or, for one of a person's own, as the Edit workspace).</summary>
    void Reset();

    /// <summary>Keeps the current layout under its name; called as the editor closes.</summary>
    void SaveCurrent();
}

/// <summary>Keeps a rectangle on the screens there are now.</summary>
public static class ScreenFit
{
    /// <summary>
    /// A floating panel or the window, moved back onto the screens when a monitor it was on has
    /// gone: unchanged when enough of it is visible to grab, otherwise centred on the work area,
    /// no larger than it.
    /// </summary>
    /// <param name="window">Where it was.</param>
    /// <param name="screens">The desktop: every monitor together.</param>
    /// <param name="workArea">Where to put it back: the primary monitor's work area.</param>
    public static Rect Fit(Rect window, Rect screens, Rect workArea)
    {
        if (window.IsEmpty || window.Width <= 0 || window.Height <= 0)
        {
            return window;
        }

        // Enough to grab: the title bar's strip, 100 by 30 pixels of it, on a screen.
        Rect grip = new(window.Left + (window.Width / 2) - 50, window.Top, 100, 30);
        grip.Intersect(screens);
        if (!grip.IsEmpty && grip.Width >= 40 && grip.Height >= 10)
        {
            return window;
        }

        double width = Math.Min(window.Width, workArea.Width);
        double height = Math.Min(window.Height, workArea.Height);
        return new Rect(workArea.Left + ((workArea.Width - width) / 2), workArea.Top + ((workArea.Height - height) / 2), width, height);
    }
}
