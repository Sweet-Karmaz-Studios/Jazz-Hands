using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Marks where the range of interest ends.</summary>
/// <remarks>
/// The frame at the out point is inside the range, the way every editor treats it: marking out
/// on a frame means "up to and including this one", so the stored range ends one frame later.
/// An out point at or before the in point moves the in point to the start of the sequence.
/// </remarks>
/// <param name="At">The last frame inside the range. Defaults to the playhead in a running editor.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Command("playback.set-out", Description = "Set the out point")]
public sealed record SetOutPointCommand(
    [property: Option("at", "The last frame inside; defaults to the playhead")] Flicks? At = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
