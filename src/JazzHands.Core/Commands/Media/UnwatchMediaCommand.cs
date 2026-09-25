namespace JazzHands.Core.Commands;

/// <summary>Stops watching a folder, or every folder.</summary>
/// <param name="Folder">The folder, or every watch when left out.</param>
[Command("media.unwatch", Description = "Stop watching a folder",
    Undoable = false,
    NotUndoableReason = "It stops a watch; nothing in the project changes.")]
public sealed record UnwatchMediaCommand(
    [property: Arg(0, "The folder, or every one when left out")] string? Folder = null) : ICommand;
