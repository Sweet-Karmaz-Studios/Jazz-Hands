using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves the playhead.</summary>
/// <remarks>
/// Playing carries on from the new position; paused stays paused there. A run of seeks close
/// together is a scrub, which is what drops the Auto preview quality to Half until it settles.
/// </remarks>
/// <param name="To">Where to put the playhead.</param>
[Command("playback.seek",
    Description = "Move the playhead",
    Undoable = false,
    NotUndoableReason = "Playback moves the playhead. It does not change the project.")]
public sealed record SeekCommand(
    [property: Arg(0, "Where to put the playhead")] Flicks To) : ICommand;
