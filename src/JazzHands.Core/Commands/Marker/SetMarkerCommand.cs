using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Changes a marker. Only the members given are changed.</summary>
/// <param name="MarkerId">Which marker.</param>
/// <param name="Name">Its label.</param>
/// <param name="At">Where it sits.</param>
/// <param name="Duration">Non-zero for a range marker.</param>
/// <param name="Color">A hex colour or a name.</param>
/// <param name="Note">Longer text, shown on hover.</param>
/// <param name="IsChapter">Export this marker as a chapter.</param>
[Command("marker.set", Description = "Change a marker")]
public sealed record SetMarkerCommand(
    [property: Arg(0, "The marker id")] string MarkerId,
    [property: Option("name", "Its label")] string? Name = null,
    [property: Option("at", "Where it sits")] Flicks? At = null,
    [property: Option("dur", "Non-zero for a range marker")] Flicks? Duration = null,
    [property: Option("color", "A hex colour or a name such as red")] string? Color = null,
    [property: Option("note", "Longer text, shown on hover")] string? Note = null,
    [property: Option("chapter", "Export this marker as a chapter")] bool? IsChapter = null) : ICommand;
