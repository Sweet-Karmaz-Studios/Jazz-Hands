using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What the scopes read on the frame at a time: histograms, clipping and the luma spread.</summary>
/// <remarks>
/// Measured on the delivered signal (BT.1886, eight bits), at full quality, as the Scopes panel
/// shows it; the pictures stay in the panel, the numbers come here.
/// </remarks>
/// <param name="At">When, on the timeline.</param>
/// <param name="SequenceId">Which sequence; the active one when not given.</param>
[Query("scopes.measure", Description = "Measure a frame's histograms and clipping")]
public sealed record MeasureScopesQuery(
    [property: Option("at", "When, on the timeline")] Flicks At,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : IQuery<ScopeSummary>;
