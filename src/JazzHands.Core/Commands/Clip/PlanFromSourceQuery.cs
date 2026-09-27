using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What an edit from the source monitor would do, without doing it.</summary>
/// <remarks>
/// The same reading of the marks <c>clip.insert-from-source</c> and <c>clip.overwrite-from-source</c>
/// make, with the same options, and what it would say: a four-point edit is fitted to the shorter
/// range, and this is where that is told.
/// </remarks>
/// <param name="MediaId">The media item; the source monitor's when left out.</param>
/// <param name="SourceIn">The source in; the source monitor's mark when left out.</param>
/// <param name="SourceOut">The source out, exclusive; the source monitor's mark when left out.</param>
/// <param name="At">Where on the timeline when there is no in point; the playhead when left out.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
[Query("clip.plan-from-source", Description = "What an edit from the source monitor would put where")]
public sealed record PlanFromSourceQuery(
    [property: Option("media", "The media item; the source monitor's when left out")] string? MediaId = null,
    [property: Option("source-in", "The source in; the source monitor's mark when left out")] Flicks? SourceIn = null,
    [property: Option("source-out", "The source out, exclusive; the source monitor's mark when left out")] Flicks? SourceOut = null,
    [property: Option("at", "Where on the timeline when there is no in point; the playhead when left out")] Flicks? At = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : IQuery<SourceEditPlan>;

/// <summary>What an edit from the source would do.</summary>
/// <param name="MediaId">The media item.</param>
/// <param name="SourceIn">Where it starts in the file.</param>
/// <param name="Duration">How long it runs.</param>
/// <param name="At">Where it starts on the timeline.</param>
/// <param name="PictureTrackId">The track the picture goes to, or null when none does.</param>
/// <param name="SoundTrackIds">The track each sound stream goes to, in stream order; empty for a stream left out, and "new" for a track the edit makes.</param>
/// <param name="Note">What was decided for the person, or null.</param>
public sealed record SourceEditPlan(
    string MediaId,
    Flicks SourceIn,
    Flicks Duration,
    Flicks At,
    string? PictureTrackId,
    EquatableArray<string> SoundTrackIds,
    string? Note);
