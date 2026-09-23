using JazzHands.Core.Export;

namespace JazzHands.Core.Commands;

/// <summary>
/// Plans an export without running it: the mode, the encoders, the stretches, the keyframes a
/// copy would snap to, and why. What <c>jazz export --dry-run</c> prints.
/// </summary>
/// <param name="Output">Where the file would go.</param>
/// <param name="Preset">Which preset.</param>
/// <param name="Mode">copy, encode, or auto.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="SnapToKeyframes">For a copy, move cuts to the nearest keyframe instead of refusing.</param>
/// <param name="UseInOut">Export only between the in and out points.</param>
/// <param name="External">Encode through ffmpeg.exe.</param>
[Query("export.plan", Description = "Plan an export without running it")]
public sealed record PlanExportQuery(
    [property: Arg(0, "Where the file would go")] string Output,
    [property: Option("preset", "Which preset")] string Preset = ExportPresets.Default,
    [property: Option("mode", "copy, encode, or auto")] ExportMode Mode = ExportMode.Auto,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("snap-to-keyframes", "For a copy, move cuts to the nearest keyframe")] bool SnapToKeyframes = false,
    [property: Option("use-in-out", "Export only between the in and out points")] bool UseInOut = false,
    [property: Option("use-external-ffmpeg", "Encode through ffmpeg.exe")] bool External = false) : IQuery<ExportPlan>
{
    /// <summary>The export this asks about.</summary>
    public ExportRequest ToRequest() => new(Output, Preset, Mode, SequenceId, SnapToKeyframes, UseInOut, External);
}
