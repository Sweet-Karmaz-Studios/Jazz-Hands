namespace JazzHands.Core.Commands;

/// <summary>Changes what a title says.</summary>
/// <remarks>
/// The text is markup (<c>[b]</c>, <c>[i]</c>, <c>[u]</c>, <c>[color=#hex]</c>, <c>[size=n]</c>,
/// <c>[font=name]</c>, <c>\n</c>); with <paramref name="Plain"/> it is taken exactly as typed, a
/// bracket being a bracket. A title whose text is keyframed is refused: <c>param.set</c> with
/// <c>--at</c> changes one keyframe.
/// </remarks>
/// <param name="ClipId">The title clip.</param>
/// <param name="Text">The new text.</param>
/// <param name="Plain">Take the text as it is, with no markup.</param>
[Command("title.set-text", Description = "Change what a title says")]
public sealed record SetTitleTextCommand(
    [property: Arg(0, "The title clip id")] string ClipId,
    [property: Arg(1, "The text, as markup: [b]bold[/b], [color=#FFCC00]gold[/color], \\n for a new line")] string Text,
    [property: Option("plain", "Take the text exactly as typed, with no markup")] bool Plain = false) : IMergeableCommand
{
    /// <inheritdoc />
    public bool Continues(ICommand previous) => previous is SetTitleTextCommand before && before.ClipId == ClipId;
}
