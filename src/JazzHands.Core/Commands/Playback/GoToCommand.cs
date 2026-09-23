namespace JazzHands.Core.Commands;

/// <summary>Sends the playhead somewhere by name: the start, the next edit, the out point.</summary>
/// <param name="Target">Where to go.</param>
[Command("playback.go-to",
    Description = "Send the playhead to the start, the end, an edit or a marker",
    Undoable = false,
    NotUndoableReason = "Playback moves the playhead. It does not change the project.")]
public sealed record GoToCommand(
    [property: Arg(0, "start, end, next-edit, prev-edit, next-marker, prev-marker, in or out")] GoToTarget Target) : ICommand;
