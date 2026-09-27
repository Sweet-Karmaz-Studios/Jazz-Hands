using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Marks the source's out point: the last frame a three-point edit takes from it.</summary>
/// <param name="At">Where, in source time; the source playhead when left out. The frame there is taken.</param>
[Command("source.set-out",
    Description = "Mark the source's out point",
    Undoable = false,
    NotUndoableReason = "The source marks are the viewer's, not the project's.")]
public sealed record SetSourceOutCommand(
    [property: Option("at", "Where, in source time; defaults to the source playhead")] Flicks? At = null) : ICommand;
