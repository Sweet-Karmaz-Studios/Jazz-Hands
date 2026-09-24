using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>One parameter: its type, limits, keyframes, and what it is worth at a time.</summary>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
/// <param name="Param">The parameter name.</param>
/// <param name="At">Also say what it is worth at this time on the sequence.</param>
[Query("param.get", Description = "Show one parameter, and its value at a time")]
public sealed record GetParamQuery(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId,
    [property: Arg(1, "The parameter name")] string Param,
    [property: Option("at", "Also say what it is worth at this time on the sequence")] Flicks? At = null) : IQuery<ParamInfo>;
