using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Where a title's text sits on the frame, and whether it stays inside title safe.</summary>
/// <remarks>
/// Laid out exactly as it is drawn, at a moment: the clip's middle when none is given, where its
/// animations are at rest. The corners include everything that moves the text (the title's own
/// offset and zoom, and the clip's transform), so they are where the text is on the picture.
/// </remarks>
/// <param name="ClipId">The title clip.</param>
/// <param name="At">When, on the sequence.</param>
[Query("title.measure", Description = "Where a title's text sits on the frame, and whether it stays inside title safe")]
public sealed record MeasureTitleQuery(
    [property: Arg(0, "The title clip id")] string ClipId,
    [property: Option("at", "When, on the sequence; the clip's middle when left out")] Flicks? At = null) : IQuery<TitleMeasureInfo>;
