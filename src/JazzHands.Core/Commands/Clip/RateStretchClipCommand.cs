using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Stretches a clip to a new duration by changing its speed.</summary>
/// <remarks>The clip shows exactly the same source material, played faster or slower to fit.</remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="ToDuration">How long it should become.</param>
[Command("clip.rate-stretch", Description = "Stretch a clip to a new duration by changing its speed")]
public sealed record RateStretchClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("dur", "How long it should become")] Flicks ToDuration) : ICommand;
