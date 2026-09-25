namespace JazzHands.Core.Commands;

/// <summary>Deletes one of a person's export presets. A built-in one of the same name comes back.</summary>
/// <param name="Name">The preset.</param>
[Command("presets.delete",
    Description = "Delete one of your export presets",
    Undoable = false,
    NotUndoableReason = "A preset is a file in your settings, not part of the project.",
    Standalone = true)]
public sealed record DeletePresetCommand(
    [property: Arg(0, "The preset")] string Name) : ICommand;
