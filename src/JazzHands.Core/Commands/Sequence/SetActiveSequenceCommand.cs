namespace JazzHands.Core.Commands;

/// <summary>Chooses the sequence the editor shows.</summary>
/// <param name="SequenceId">Which sequence.</param>
[Command("sequence.set-active", Description = "Choose the sequence the editor shows")]
public sealed record SetActiveSequenceCommand(
    [property: Arg(0, "The sequence id")] string SequenceId) : ICommand;
