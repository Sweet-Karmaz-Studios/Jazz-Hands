namespace JazzHands.Core.Commands;

/// <summary>Adds an empty sequence with one video and one audio track.</summary>
/// <param name="Name">Its display name.</param>
/// <param name="SetActive">Make it the sequence the editor shows.</param>
/// <param name="SequenceId">The identifier to give it. A fresh one when left out.</param>
[Command("sequence.create", Description = "Add an empty sequence")]
public sealed record CreateSequenceCommand(
    [property: Arg(0, "The sequence name")] string Name,
    [property: Option("set-active", "Show it in the editor")] bool SetActive = true,
    [property: Option("id", "The identifier to give it")] string? SequenceId = null) : ICommand;
