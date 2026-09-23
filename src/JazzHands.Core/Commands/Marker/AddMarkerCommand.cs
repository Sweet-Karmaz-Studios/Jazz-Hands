using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts a marker on a sequence or on a clip.</summary>
/// <remarks>
/// A marker on a clip is positioned relative to the clip start, so it travels with the clip. A
/// marker on a sequence is at an absolute timeline time.
/// </remarks>
/// <param name="At">Where it sits.</param>
/// <param name="Name">Its label.</param>
/// <param name="Duration">Non-zero for a range marker.</param>
/// <param name="Color">A hex colour or a name.</param>
/// <param name="Note">Longer text, shown on hover.</param>
/// <param name="IsChapter">Export this marker as a chapter.</param>
/// <param name="ClipId">Put it on this clip instead of on the sequence.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="MarkerId">The identifier to give it. A fresh one when left out.</param>
[Command("marker.add", Description = "Put a marker on a sequence or a clip")]
public sealed record AddMarkerCommand(
    [property: Option("at", "Where it sits")] Flicks At,
    [property: Arg(0, "Its label")] string Name = "",
    [property: Option("dur", "Non-zero for a range marker")] Flicks? Duration = null,
    [property: Option("color", "A hex colour or a name such as red")] string? Color = null,
    [property: Option("note", "Longer text, shown on hover")] string? Note = null,
    [property: Option("chapter", "Export this marker as a chapter")] bool IsChapter = false,
    [property: Option("clip", "Put it on this clip instead")] string? ClipId = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("id", "The identifier to give it")] string? MarkerId = null) : ICommand;
