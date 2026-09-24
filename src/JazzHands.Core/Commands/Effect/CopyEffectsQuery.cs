using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>
/// Copies effects as JSON for <c>effect.paste</c>. An owner's id copies its whole chain.
/// </summary>
/// <param name="Ids">Effect ids, or the id of a clip or track for all of its effects.</param>
[Query("effect.copy", Description = "Copy effects as JSON for effect.paste")]
public sealed record CopyEffectsQuery(
    [property: Arg(0, "Comma-separated effect ids, or a clip or track id for its whole chain")] EquatableArray<string> Ids) : IQuery<string>;
