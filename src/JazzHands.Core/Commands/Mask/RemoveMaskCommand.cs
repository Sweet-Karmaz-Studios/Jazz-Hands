namespace JazzHands.Core.Commands;

/// <summary>Takes a mask off its clip.</summary>
/// <param name="MaskId">Which mask.</param>
[Command("mask.remove", Description = "Remove a mask")]
public sealed record RemoveMaskCommand(
    [property: Arg(0, "The mask id")] string MaskId) : ICommand;
