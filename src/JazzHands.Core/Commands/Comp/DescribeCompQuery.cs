namespace JazzHands.Core.Commands;

/// <summary>A comp graph as text a reader can follow (Phase 49): each node the output needs, what it does and what it reads, then any nodes it does not use.</summary>
/// <param name="Target">A comp graph, or a clip whose graph it is.</param>
[Query("comp.describe", Description = "Describe a clip's comp graph in readable text")]
public sealed record DescribeCompQuery(
    [property: Arg(0, "A comp graph, or a clip whose graph it is")] string Target) : IQuery<string>;
