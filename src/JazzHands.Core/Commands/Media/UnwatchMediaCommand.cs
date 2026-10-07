namespace JazzHands.Core.Commands;

/// <summary>Stops watching a folder, or every folder.</summary>
/// <remarks>The watch leaves the project too, so opening it again does not bring it back; undo does.</remarks>
/// <param name="Folder">The folder, or every watch when left out.</param>
[Command("media.unwatch", Description = "Stop watching a folder")]
public sealed record UnwatchMediaCommand(
    [property: Arg(0, "The folder, or every one when left out")] string? Folder = null) : ICommand;
