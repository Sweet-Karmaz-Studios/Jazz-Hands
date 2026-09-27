using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Puts the source's marked stretch into the sequence, pushing what is after it along.</summary>
/// <remarks>
/// A three-point edit (see ThreePointOps): the source monitor's marks, the sequence's in and out
/// and the playhead decide what goes where; every option overrides one of them, so a script can
/// do the whole edit without a source monitor. The picture and sound go to the targeted tracks
/// (<c>track.set-target</c>), linked. One undo.
/// </remarks>
/// <param name="MediaId">The media item; the source monitor's when left out.</param>
/// <param name="SourceIn">The source in; the source monitor's mark when left out.</param>
/// <param name="SourceOut">The source out, exclusive; the source monitor's mark when left out.</param>
/// <param name="At">Where on the timeline, when the sequence has no in point; the playhead when left out.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Command("clip.insert-from-source", Description = "Insert the source's marked stretch at the playhead or the in point")]
public sealed record InsertFromSourceCommand(
    [property: Option("media", "The media item; the source monitor's when left out")] string? MediaId = null,
    [property: Option("source-in", "The source in; the source monitor's mark when left out")] Flicks? SourceIn = null,
    [property: Option("source-out", "The source out, exclusive; the source monitor's mark when left out")] Flicks? SourceOut = null,
    [property: Option("at", "Where on the timeline when there is no in point; the playhead when left out")] Flicks? At = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
