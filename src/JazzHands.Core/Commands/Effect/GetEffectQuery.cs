namespace JazzHands.Core.Commands;

/// <summary>One effect on a clip or a track: its type, place and every parameter's value and keyframes.</summary>
/// <param name="EffectId">Which effect.</param>
[Query("effect.get", Description = "Show one effect and its parameters")]
public sealed record GetEffectQuery(
    [property: Arg(0, "The effect id")] string EffectId) : IQuery<EffectInfo>;
