namespace JazzHands.Core.Commands;

/// <summary>Plays from the playhead at normal speed.</summary>
/// <remarks>
/// Like every playback command, this needs a running editor: a headless <c>jazz</c> process has
/// no transport to start, and says so with the code <c>no-playback</c>.
/// </remarks>
[Command("playback.play",
    Description = "Play from the playhead",
    Undoable = false,
    NotUndoableReason = "Playback moves the playhead. It does not change the project.")]
public sealed record PlayCommand : ICommand;
