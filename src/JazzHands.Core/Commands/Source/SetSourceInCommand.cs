using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Marks the source's in point: where a three-point edit starts taking from it.</summary>
/// <param name="At">Where, in source time; the source playhead when left out.</param>
[Command("source.set-in",
    Description = "Mark the source's in point",
    Undoable = false,
    NotUndoableReason = "The source marks are the viewer's, not the project's.")]
public sealed record SetSourceInCommand(
    [property: Option("at", "Where, in source time; defaults to the source playhead")] Flicks? At = null) : ICommand;
