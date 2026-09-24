using JazzHands.Core.Effects;

namespace JazzHands.Core.Commands;

/// <summary>Every effect type there is, with its parameters, their types, defaults and limits.</summary>
/// <param name="Kind">Only picture effects, sound effects or generators.</param>
/// <param name="Search">Only types whose id, name or category contain this.</param>
[Query("effect.list", Description = "List the effect types and their parameters")]
public sealed record ListEffectsQuery(
    [property: Option("kind", "Only video, audio or generator")] EffectKind? Kind = null,
    [property: Option("search", "Only types whose id, name or category contain this")] string? Search = null) : IQuery<EffectTypeInfo[]>;
