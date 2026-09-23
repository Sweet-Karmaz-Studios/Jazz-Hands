namespace JazzHands.Core.Commands;

/// <summary>Changes a sequence's display name.</summary>
/// <param name="SequenceId">Which sequence.</param>
/// <param name="Name">Its new name.</param>
[Command("sequence.rename", Description = "Rename a sequence")]
public sealed record RenameSequenceCommand(
    [property: Arg(0, "The sequence id")] string SequenceId,
    [property: Arg(1, "The new name")] string Name) : ICommand;
