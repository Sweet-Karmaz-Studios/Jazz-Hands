namespace JazzHands.Core.Commands;

/// <summary>Removes a sequence.</summary>
/// <remarks>
/// Refused while another sequence nests this one as a compound clip, because the clip would be
/// left pointing at nothing. Remove the compound clip first.
/// </remarks>
/// <param name="SequenceId">Which sequence.</param>
[Command("sequence.remove", Description = "Remove a sequence")]
public sealed record RemoveSequenceCommand(
    [property: Arg(0, "The sequence id")] string SequenceId) : ICommand;
