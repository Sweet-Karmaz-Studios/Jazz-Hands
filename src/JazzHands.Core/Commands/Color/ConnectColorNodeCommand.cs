namespace JazzHands.Core.Commands;

/// <summary>Connects one node of a colour graph to another (Phase 44).</summary>
/// <remarks>
/// The node reads <paramref name="From"/>'s picture from now on: as its input, as one of a mix's
/// inputs (<paramref name="Input"/>, from 1; a new one at the end when not given, up to four), or,
/// with <paramref name="Key"/>, as its key, a qualifier node whose matte limits where it applies.
/// A connection that would go round in a circle is refused. One undo step.
/// </remarks>
/// <param name="NodeId">The node that reads.</param>
/// <param name="From">The node it reads; the picture coming into the graph when not given.</param>
/// <param name="Input">Which of a mix's inputs, from 1.</param>
/// <param name="Key">Connect as its key rather than its picture.</param>
[Command("color.node-connect", Description = "Connect one node of a colour graph to another")]
public sealed record ConnectColorNodeCommand(
    [property: Arg(0, "The node that reads")] string NodeId,
    [property: Option("from", "The node it reads; the picture coming into the graph when not given")] string? From = null,
    [property: Option("input", "Which of a mix's inputs, from 1; a new one at the end when not given")] int? Input = null,
    [property: Option("key", "Connect as its key: a qualifier node whose matte limits where it applies")] bool Key = false) : ICommand;
