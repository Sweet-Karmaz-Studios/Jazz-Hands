using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts the source's marked stretch over what the sequence has there.</summary>
/// <remarks>As <c>clip.insert-from-source</c>, but nothing moves: what was under it is replaced. One undo.</remarks>
/// <param name="MediaId">The media item; the source monitor's when left out.</param>
/// <param name="SourceIn">The source in; the source monitor's mark when left out.</param>
/// <param name="SourceOut">The source out, exclusive; the source monitor's mark when left out.</param>
/// <param name="At">Where on the timeline, when the sequence has no in point; the playhead when left out.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Command("clip.overwrite-from-source", Description = "Overwrite with the source's marked stretch at the playhead or the in point")]
public sealed record OverwriteFromSourceCommand(
    [property: Option("media", "The media item; the source monitor's when left out")] string? MediaId = null,
    [property: Option("source-in", "The source in; the source monitor's mark when left out")] Flicks? SourceIn = null,
    [property: Option("source-out", "The source out, exclusive; the source monitor's mark when left out")] Flicks? SourceOut = null,
    [property: Option("at", "Where on the timeline when there is no in point; the playhead when left out")] Flicks? At = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
