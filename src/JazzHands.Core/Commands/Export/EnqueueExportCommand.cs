using JazzHands.Core.Export;
using JazzHands.Core.Subtitles;

namespace JazzHands.Core.Commands;

/// <summary>Plans an export and puts it in the export queue.</summary>
/// <remarks>
/// The plan is made against the project as it is when the command runs and stored with the job,
/// so editing on while it waits changes nothing about the file it writes. The job id comes back
/// as the command's changed id, the way a new clip's does. A headless session has no queue; the
/// CLI's <c>jazz export</c> and <c>jazz trim</c> export in the foreground instead.
/// </remarks>
/// <param name="Output">Where to write the file.</param>
/// <param name="Preset">Which preset.</param>
/// <param name="Mode">copy, encode, or auto to let the planner choose.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="SnapToKeyframes">For a copy, move cuts to the nearest keyframe instead of refusing.</param>
/// <param name="UseInOut">Export only between the in and out points.</param>
/// <param name="External">Encode through ffmpeg.exe instead of in process.</param>
/// <param name="Subtitles">What happens to subtitle tracks: streams in the file (soft), burned in, files beside it (sidecar), or none.</param>
/// <param name="SidecarFormat">The format of subtitle files written beside the video.</param>
/// <param name="Chapters">Write the sequence's chapter marks into the file.</param>
/// <param name="JobId">The id for the new job. A fresh one when left out.</param>
[Command("export.enqueue",
    Description = "Queue an export of a sequence to a file",
    Undoable = false,
    NotUndoableReason = "An export writes a file. It does not change the project.")]
public sealed record EnqueueExportCommand(
    [property: Arg(0, "Where to write the file")] string Output,
    [property: Option("preset", "Which preset: youtube-1080p, youtube-4k, proof, lossless")] string Preset = ExportPresets.Default,
    [property: Option("mode", "copy, encode, or auto")] ExportMode Mode = ExportMode.Auto,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("snap-to-keyframes", "For a copy, move cuts to the nearest keyframe")] bool SnapToKeyframes = false,
    [property: Option("use-in-out", "Export only between the in and out points")] bool UseInOut = false,
    [property: Option("use-external-ffmpeg", "Encode through ffmpeg.exe")] bool External = false,
    [property: Option("subtitles", "soft, burn, sidecar or none")] SubtitleDelivery Subtitles = SubtitleDelivery.Soft,
    [property: Option("sidecar-format", "srt, vtt or ass")] SubtitleFormat SidecarFormat = SubtitleFormat.Srt,
    [property: Option("chapters", "Write chapter marks into the file")] bool Chapters = true,
    [property: Option("id", "The id for the new job")] string? JobId = null) : ICommand
{
    /// <summary>The export this asks for.</summary>
    public ExportRequest ToRequest() => new(Output, Preset, Mode, SequenceId, SnapToKeyframes, UseInOut, External, Subtitles, SidecarFormat, Chapters);
}
