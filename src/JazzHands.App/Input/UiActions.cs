using JazzHands.App.ViewModels.Timeline;

namespace JazzHands.App.Input;

/// <summary>
/// Keymap actions that belong to the editor rather than the engine: picking a tool, snapping,
/// and the clipboard, which is Windows' and not the project's.
/// </summary>
/// <remarks>
/// Named with a <c>ui.</c> prefix so that no engine command can ever be mistaken for one. They
/// change how the mouse behaves or move text through the clipboard; every edit they lead to is
/// still an engine command.
/// </remarks>
public static class UiActions
{
    /// <summary>What marks a binding as a UI action.</summary>
    public const string Prefix = "ui.";

    /// <summary>Every UI action a key can be bound to.</summary>
    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(
        [
            .. TimelineTools.All.Select(tool => tool.Action),
            "ui.snap",
            "ui.copy",
            "ui.cut",
            "ui.paste",
            "ui.paste-insert",
            "ui.match-frame",
        ],
        StringComparer.Ordinal);

    /// <summary>True for a binding's command that is a UI action rather than an engine command.</summary>
    public static bool IsAction(string command) => command.StartsWith(Prefix, StringComparison.Ordinal);
}
