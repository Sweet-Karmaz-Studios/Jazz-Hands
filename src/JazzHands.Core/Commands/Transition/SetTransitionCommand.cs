using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Changes a transition's type, duration or alignment.</summary>
/// <remarks>
/// What is left out stays as it is. A new type keeps the parameters the two types share and drops
/// the rest. The duration and alignment change the transition on the linked picture or sound at
/// the same cut too, unless <paramref name="Linked"/> is false, so picture and sound keep turning
/// over together. Dragging a transition's edge on the timeline sends one of these per step, and
/// they merge into one undo step.
/// </remarks>
/// <param name="TransitionId">Which transition.</param>
/// <param name="Type">A different transition of the same kind.</param>
/// <param name="Duration">How long it runs.</param>
/// <param name="Alignment">Where it sits on the cut.</param>
/// <param name="Linked">Also change the linked transition's duration and alignment.</param>
[Command("transition.set", Description = "Change a transition's type, duration or alignment")]
public sealed record SetTransitionCommand(
    [property: Arg(0, "The transition id")] string TransitionId,
    [property: Option("type", "A different transition of the same kind")] string? Type = null,
    [property: Option("dur", "How long it runs")] Flicks? Duration = null,
    [property: Option("alignment", "centered, end-of-left or start-of-right")] TransitionAlignment? Alignment = null,
    [property: Option("linked", "Also change the linked transition's duration and alignment")] bool Linked = true) : IMergeableCommand
{
    /// <inheritdoc />
    public bool Continues(ICommand previous) =>
        previous is SetTransitionCommand before
        && before.TransitionId == TransitionId
        && before.Linked == Linked
        && Type is null && before.Type is null
        && Duration is not null && before.Duration is not null
        && Alignment == before.Alignment;
}
