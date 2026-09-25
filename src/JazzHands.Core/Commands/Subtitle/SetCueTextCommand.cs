namespace JazzHands.Core.Commands;

/// <summary>Changes what a subtitle cue says.</summary>
/// <remarks>Typing into a cue sends this for each pause; consecutive changes to one cue are one undo step.</remarks>
/// <param name="CueId">The cue.</param>
/// <param name="Text">What it says, as markup.</param>
[Command("subtitle.set-text", Description = "Change what a subtitle cue says")]
public sealed record SetCueTextCommand(
    [property: Arg(0, "The cue id")] string CueId,
    [property: Option("text", "What it says, as markup")] string Text) : IMergeableCommand
{
    /// <inheritdoc />
    public bool Continues(ICommand previous) => previous is SetCueTextCommand before && before.CueId == CueId;
}
