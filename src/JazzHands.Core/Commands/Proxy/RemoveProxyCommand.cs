namespace JazzHands.Core.Commands;

/// <summary>Deletes proxy files. The media they stood in for is untouched.</summary>
/// <param name="MediaId">One media item.</param>
/// <param name="All">Every media item in the project.</param>
[Command("proxy.remove",
    Description = "Delete proxy files",
    Undoable = false,
    NotUndoableReason = "Deleting a file cannot be undone; generate the proxy again instead.")]
public sealed record RemoveProxyCommand(
    [property: Option("media", "The media id")] string? MediaId = null,
    [property: Option("all", "Every media item in the project")] bool All = false) : ICommand;
