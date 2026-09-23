namespace JazzHands.Core.Commands;

/// <summary>Removes a marker, wherever it is.</summary>
/// <param name="MarkerId">Which marker.</param>
[Command("marker.remove", Description = "Remove a marker")]
public sealed record RemoveMarkerCommand(
    [property: Arg(0, "The marker id")] string MarkerId) : ICommand;
