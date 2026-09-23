namespace JazzHands.Core.Commands;

/// <summary>Removes the in and out points.</summary>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Command("playback.clear-in-out", Description = "Remove the in and out points")]
public sealed record ClearInOutCommand(
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
