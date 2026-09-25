using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Writes the frame at a time as a PNG, drawn as an export would draw it. It runs straight away,
/// in any session, rather than going through the queue: one frame takes a moment.
/// </summary>
/// <param name="Output">The .png file to write.</param>
/// <param name="At">The sequence time; the frame that holds it is written.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="Size">Fit the picture inside this size; the sequence's own when left out.</param>
[Command("export.still",
    Description = "Write the frame at a time as a PNG",
    Undoable = false,
    NotUndoableReason = "It writes a file. It does not change the project.")]
public sealed record ExportStillCommand(
    [property: Arg(0, "The .png file to write")] string Output,
    [property: Option("at", "The sequence time to draw")] Flicks At,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("size", "Fit the picture inside this size, for example 1280x720")] FrameSize? Size = null) : ICommand;
