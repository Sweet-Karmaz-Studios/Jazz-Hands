using JazzHands.App.ViewModels.Timeline;

namespace JazzHands.App.Input;

/// <summary>
/// Keymap actions that belong to the editor rather than the engine: picking a tool, snapping,
/// the clipboard, and the window's own dialogs (new, open, save, import, export, settings).
/// </summary>
/// <remarks>
/// Named with a <c>ui.</c> prefix so that no engine command can ever be mistaken for one. They
/// change how the mouse behaves, move text through the clipboard or open a dialog; every edit
/// they lead to is still an engine command.
/// </remarks>
public static class UiActions
{
    /// <summary>What marks a binding as a UI action.</summary>
    public const string Prefix = "ui.";

    /// <summary>The window's actions, which the main window handles whatever panel is in front.</summary>
    public static IReadOnlyDictionary<string, string> Window { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ui.new"] = "New project",
        ["ui.open"] = "Open a project",
        ["ui.save"] = "Save the project",
        ["ui.save-as"] = "Save the project as",
        ["ui.import"] = "Import media",
        ["ui.export"] = "Export",
        ["ui.settings"] = "Settings",
        ["ui.insert-from-source"] = "Insert the source monitor's marked stretch",
        ["ui.overwrite-from-source"] = "Overwrite with the source monitor's marked stretch",
    };

    /// <summary>The timeline's actions, which the timeline in front handles.</summary>
    public static IReadOnlyDictionary<string, string> Timeline { get; } = new Dictionary<string, string>(
        [
            .. TimelineTools.All.Select(tool => new KeyValuePair<string, string>(tool.Action, $"{tool.Label} tool")),
            new("ui.snap", "Snapping on or off"),
            new("ui.copy", "Copy clips"),
            new("ui.cut", "Cut clips"),
            new("ui.paste", "Paste clips over"),
            new("ui.paste-insert", "Paste clips, pushing the rest on"),
            new("ui.match-frame", "Match frame: show the source frame"),
        ],
        StringComparer.Ordinal);

    /// <summary>Every UI action a key can be bound to.</summary>
    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(Window.Keys.Concat(Timeline.Keys), StringComparer.Ordinal);

    /// <summary>True for a binding's command that is a UI action rather than an engine command.</summary>
    public static bool IsAction(string command) => command.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>What a UI action does, or null for one that is not.</summary>
    public static string? Describe(string command) =>
        Window.TryGetValue(command, out string? window) ? window
        : Timeline.TryGetValue(command, out string? timeline) ? timeline
        : null;
}
