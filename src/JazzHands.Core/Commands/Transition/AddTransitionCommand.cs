using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What adding a transition does when a clip has less source beyond the cut than it needs.</summary>
public enum TransitionHandles
{
    /// <summary>Refuse, saying how much is missing.</summary>
    Refuse,

    /// <summary>Trim the clips back from the cut until they have the source, rippling what follows.</summary>
    Trim,

    /// <summary>Add it anyway: where the source runs out the transition holds the last frame there is.</summary>
    Hold,
}

/// <summary>Puts a transition on the cut between two adjacent clips.</summary>
/// <remarks>
/// The clips must be on one track, the first ending where the second starts. Inside the
/// transition the outgoing clip plays on past its end and the incoming one starts early, so each
/// needs source beyond the cut; <paramref name="Handles"/> says what happens when a file has too
/// little. A picture transition also crossfades the clips' linked sound where it meets at the
/// same cut, unless <paramref name="Audio"/> is false. The type and duration default to the
/// project's (<c>transition.set-default</c>).
/// </remarks>
/// <param name="LeftClipId">The outgoing clip.</param>
/// <param name="RightClipId">The incoming clip.</param>
/// <param name="Type">Which transition, as <c>jazz effect list</c> shows them.</param>
/// <param name="Duration">How long it runs.</param>
/// <param name="Alignment">Where it sits on the cut.</param>
/// <param name="Handles">What to do when a clip has too little source beyond the cut.</param>
/// <param name="Audio">Also crossfade the linked sound at the same cut.</param>
/// <param name="TransitionId">The identifier to give it. A fresh one when left out.</param>
[Command("transition.add", Description = "Put a transition on the cut between two clips")]
public sealed record AddTransitionCommand(
    [property: Arg(0, "The outgoing clip id")] string LeftClipId,
    [property: Arg(1, "The incoming clip id")] string RightClipId,
    [property: Option("type", "Which transition, for example transition.crossfade")] string? Type = null,
    [property: Option("dur", "How long it runs")] Flicks? Duration = null,
    [property: Option("alignment", "centered, end-of-left or start-of-right")] TransitionAlignment Alignment = TransitionAlignment.Centered,
    [property: Option("handles", "When a clip has too little source past the cut: refuse, trim or hold")] TransitionHandles Handles = TransitionHandles.Refuse,
    [property: Option("audio", "Also crossfade the linked sound at the same cut")] bool Audio = true,
    [property: Option("id", "The identifier to give it")] string? TransitionId = null) : ICommand;
