using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Marks where the range of interest starts: what loops, and later what exports.</summary>
/// <remarks>
/// The in and out points are part of the sequence, so this one is undoable and is saved with the
/// project. An in point at or after the out point moves the out point to the end of the sequence.
/// </remarks>
/// <param name="At">Where, on the timeline. Defaults to the playhead in a running editor.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Command("playback.set-in", Description = "Set the in point")]
public sealed record SetInPointCommand(
    [property: Option("at", "Where; defaults to the playhead")] Flicks? At = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
