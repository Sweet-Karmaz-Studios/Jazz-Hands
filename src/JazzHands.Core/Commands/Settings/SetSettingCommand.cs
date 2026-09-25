namespace JazzHands.Core.Commands;

/// <summary>Changes one of the editor's settings, as the Settings dialog would.</summary>
/// <remarks>
/// A key is a section and a name, as <c>settings.get</c> lists them (<c>editor.closeToTray</c>,
/// <c>cache.capBytes</c>). The value is JSON, or plain text for text: <c>true</c>, <c>20</c>,
/// <c>D:\cache</c>. It is checked against the setting's type before anything is written. A running
/// editor applies what it can at once; the rest, like the GPU, from its next start.
/// </remarks>
/// <param name="Key">The setting, as section.name.</param>
/// <param name="Value">Its new value.</param>
[Command("settings.set",
    Description = "Change one of the editor's settings",
    Undoable = false,
    NotUndoableReason = "Settings belong to the person, not the project; set the old value back instead.")]
public sealed record SetSettingCommand(
    [property: Arg(0, "The setting, as section.name (see settings.get)")] string Key,
    [property: Arg(1, "Its new value: JSON, or plain text")] string Value) : ICommand;
