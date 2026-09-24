namespace JazzHands.Core.Commands;

/// <summary>Sets one of a transition's parameters.</summary>
/// <remarks>The same as <c>param.set</c> with the transition's id.</remarks>
/// <param name="TransitionId">Which transition.</param>
/// <param name="Param">The parameter name, as <c>jazz effect list</c> shows it.</param>
/// <param name="Value">The value, as text: 45, "#000000", ease-in-out, "0.2, 0, 0.2, 1".</param>
[Command("transition.set-param", Description = "Set a parameter of a transition")]
public sealed record SetTransitionParamCommand(
    [property: Arg(0, "The transition id")] string TransitionId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Arg(2, "The value, for example 45, #000000 or ease-in-out")] string Value) : IMergeableCommand
{
    /// <inheritdoc />
    public bool Continues(ICommand previous) =>
        previous is SetTransitionParamCommand before && before.TransitionId == TransitionId && before.Param == Param;
}
