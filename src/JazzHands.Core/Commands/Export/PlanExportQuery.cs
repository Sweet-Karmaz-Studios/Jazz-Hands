using JazzHands.Core.Export;
using JazzHands.Core.Subtitles;

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
/// <param name="Subtitles">What happens to subtitle tracks: streams in the file (soft), burned in, files beside it (sidecar), or none.</param>
/// <param name="SidecarFormat">The format of subtitle files written beside the video.</param>
/// <param name="Chapters">Write the sequence's chapter marks into the file.</param>
[Query("export.plan", Description = "Plan an export without running it")]
public sealed record PlanExportQuery(
    [property: Arg(0, "Where the file would go")] string Output,
    [property: Option("preset", "Which preset")] string Preset = ExportPresets.Default,
    [property: Option("mode", "copy, encode, or auto")] ExportMode Mode = ExportMode.Auto,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("snap-to-keyframes", "For a copy, move cuts to the nearest keyframe")] bool SnapToKeyframes = false,
    [property: Option("use-in-out", "Export only between the in and out points")] bool UseInOut = false,
    [property: Option("use-external-ffmpeg", "Encode through ffmpeg.exe")] bool External = false,
    [property: Option("subtitles", "soft, burn, sidecar or none")] SubtitleDelivery Subtitles = SubtitleDelivery.Soft,
    [property: Option("sidecar-format", "srt, vtt or ass")] SubtitleFormat SidecarFormat = SubtitleFormat.Srt,
    [property: Option("chapters", "Write chapter marks into the file")] bool Chapters = true) : IQuery<ExportPlan>
{
    /// <summary>The export this asks about.</summary>
    public ExportRequest ToRequest() => new(Output, Preset, Mode, SequenceId, SnapToKeyframes, UseInOut, External, Subtitles, SidecarFormat, Chapters);
}
