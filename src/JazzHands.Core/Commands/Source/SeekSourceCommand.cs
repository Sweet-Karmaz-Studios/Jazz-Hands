using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves the source monitor's playhead.</summary>
/// <param name="At">Where, in source time.</param>
[Command("source.seek",
    Description = "Move the source monitor's playhead",
    Undoable = false,
    NotUndoableReason = "The source monitor is a viewer. It does not change the project.")]
public sealed record SeekSourceCommand(
    [property: Option("at", "Where, in source time")] Flicks At) : ICommand;
