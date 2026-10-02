namespace JazzHands.Core.Commands;

/// <summary>Adds a node to a comp graph (Phase 49).</summary>
/// <remarks>
/// <paramref name="Type"/> is one of the graph's own (<c>in</c>, <c>out</c>, <c>media</c>,
/// <c>merge</c>, <c>transform</c>, <c>matte</c>, <c>plane3d</c>, <c>render3d</c>), a 3D object
/// (<c>text3d</c>, <c>shape3d</c>, <c>model3d</c>, <c>camera3d</c>, <c>light3d</c>), or any video
/// effect or generator type (<c>video.blur.gaussian</c>, <c>gen.noise</c>, <c>color.wheels</c>).
/// With <paramref name="From"/> it is wired from that node into its first port. A graph has one
/// output; a second is refused with <c>output-exists</c>. Its parameters are set with
/// <c>param.set</c> by its id. One undo step.
/// </remarks>
/// <param name="Target">A comp graph, or a clip whose graph it is.</param>
/// <param name="Type">What the node does.</param>
/// <param name="From">A node to wire into its first port.</param>
/// <param name="X">Its left edge in the node view; next to the others when left out.</param>
/// <param name="Y">Its top edge in the node view.</param>
/// <param name="NodeId">The identifier for the node.</param>
[Command("comp.node-add", Description = "Add a node to a comp graph")]
public sealed record AddCompNodeCommand(
    [property: Arg(0, "A comp graph, or a clip whose graph it is")] string Target,
    [property: Arg(1, "What it does: merge, transform, matte, media, plane3d, render3d, text3d, out, or any effect or generator type")] string Type,
    [property: Option("from", "A node to wire into its first port")] string? From = null,
    [property: Option("x", "Its left edge in the node view")] double? X = null,
    [property: Option("y", "Its top edge in the node view")] double? Y = null,
    [property: Option("id", "The identifier for the node")] string? NodeId = null) : ICommand;
