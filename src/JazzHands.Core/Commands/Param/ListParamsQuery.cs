namespace JazzHands.Core.Commands;

/// <summary>
/// Every parameter a clip, track, effect or mask has, with its type, limits, value and keyframes.
/// </summary>
/// <param name="OwnerId">The clip, track, effect or mask.</param>
[Query("param.list", Description = "List the parameters of a clip, track, effect or mask")]
public sealed record ListParamsQuery(
    [property: Arg(0, "The clip, track, effect or mask id")] string OwnerId) : IQuery<ParamInfo[]>;
