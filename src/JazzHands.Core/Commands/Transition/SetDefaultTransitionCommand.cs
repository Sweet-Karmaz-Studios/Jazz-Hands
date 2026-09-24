using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Sets what the default transition shortcuts add: a picture or a sound type, and the duration of both.</summary>
/// <param name="Type">A picture transition sets the picture default, a sound one the sound default. Left out, only the duration changes.</param>
/// <param name="Duration">How long the default transitions last.</param>
[Command("transition.set-default", Description = "Set the transition the default shortcuts add")]
public sealed record SetDefaultTransitionCommand(
    [property: Arg(0, "The transition type")] string? Type = null,
    [property: Option("dur", "How long the default transitions last")] Flicks? Duration = null) : ICommand;
