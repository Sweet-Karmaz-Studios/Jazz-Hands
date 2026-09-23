namespace JazzHands.Core.Commands;

/// <summary>Opens a .jazz file as the session's project.</summary>
/// <param name="Path">The file to open.</param>
/// <param name="Discard">Throw away unsaved changes in the current project.</param>
[Command("project.open",
    Description = "Open a .jazz file",
    Undoable = false,
    NotUndoableReason = "Undo history belongs to a project, and this replaces the project.")]
public sealed record OpenProjectCommand(
    [property: Arg(0, "The .jazz file to open")] string Path,
    [property: Option("discard", "Throw away unsaved changes")] bool Discard = false) : ICommand;
