using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>
/// Writes a contact sheet: frames at even steps through what an export would play, tiled with the
/// sequence time under each, as one PNG. For looking at a whole cut at once.
/// </summary>
/// <param name="Output">The .png file to write.</param>
/// <param name="Columns">Tiles across.</param>
/// <param name="Rows">Tiles down.</param>
/// <param name="Width">The sheet's width in pixels.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="Start">From here, in sequence time.</param>
/// <param name="End">To here, in sequence time.</param>
[Command("export.contact-sheet",
    Description = "Write a contact sheet of frames across a sequence",
    Undoable = false,
    NotUndoableReason = "It writes a file. It does not change the project.")]
public sealed record ContactSheetCommand(
    [property: Arg(0, "The .png file to write")] string Output,
    [property: Option("columns", "Tiles across")] int Columns = 4,
    [property: Option("rows", "Tiles down")] int Rows = 4,
    [property: Option("width", "The sheet's width in pixels")] int Width = 1920,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("start", "From here")] Flicks? Start = null,
    [property: Option("end", "To here")] Flicks? End = null) : ICommand;
