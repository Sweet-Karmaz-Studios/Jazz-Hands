namespace JazzHands.Core.Commands;

/// <summary>Takes a transition off its cut, leaving a straight cut.</summary>
/// <param name="TransitionId">Which transition.</param>
/// <param name="Linked">Also remove the transition on the linked picture or sound at the same cut.</param>
[Command("transition.remove", Description = "Remove a transition")]
public sealed record RemoveTransitionCommand(
    [property: Arg(0, "The transition id")] string TransitionId,
    [property: Option("linked", "Also remove the one on the linked picture or sound at the same cut")] bool Linked = true) : ICommand;
