using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Time;

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
/// <param name="Size">Fit the picture inside this size.</param>
/// <param name="FrameRate">Write at this rate instead of the sequence's.</param>
/// <param name="Quality">Constant quality instead of the preset's.</param>
/// <param name="Bitrate">A picture bitrate instead of constant quality.</param>
/// <param name="Encoders">The encoders to try, in order.</param>
/// <param name="AudioEncoder">The sound encoder.</param>
/// <param name="AudioBitrate">The sound bitrate.</param>
/// <param name="Channels">1, 2 or 6 channels.</param>
/// <param name="Loudness">Normalise the mix to this many LUFS.</param>
/// <param name="TargetSize">Come in under this size.</param>
/// <param name="PixelFormat">The pixel format to encode, for ten bits or 4:2:2.</param>
/// <param name="Start">Export from here, in sequence time.</param>
/// <param name="End">Export to here, in sequence time.</param>
/// <param name="Priority">Where it goes in the queue: high jobs start before normal ones, normal before low.</param>
/// <param name="OpenFolder">Show the file in Explorer when it is done.</param>
/// <param name="Run">A script to run when it is done, given the file's path.</param>
/// <param name="JobId">The id for the new job. A fresh one when left out.</param>
[Command("export.enqueue",
    Description = "Queue an export of a sequence to a file",
    Undoable = false,
    NotUndoableReason = "An export writes a file. It does not change the project.")]
public sealed record EnqueueExportCommand(
    [property: Arg(0, "Where to write the file")] string Output,
    [property: Option("preset", "Which preset; jazz presets list shows them")] string Preset = ExportPresets.Default,
    [property: Option("mode", "copy, encode, or auto")] ExportMode Mode = ExportMode.Auto,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("snap-to-keyframes", "For a copy, move cuts to the nearest keyframe")] bool SnapToKeyframes = false,
    [property: Option("use-in-out", "Export only between the in and out points")] bool UseInOut = false,
    [property: Option("use-external-ffmpeg", "Encode through ffmpeg.exe")] bool External = false,
    [property: Option("subtitles", "soft, burn, sidecar or none")] SubtitleDelivery Subtitles = SubtitleDelivery.Soft,
    [property: Option("sidecar-format", "srt, vtt or ass")] SubtitleFormat SidecarFormat = SubtitleFormat.Srt,
    [property: Option("chapters", "Write chapter marks into the file")] bool Chapters = true,
    [property: Option("size", "Fit the picture inside this size, for example 1280x720")] FrameSize? Size = null,
    [property: Option("fps", "Write at this frame rate")] Rational? FrameRate = null,
    [property: Option("quality", "Constant quality: CRF or CQ, lower is better")] int? Quality = null,
    [property: Option("bitrate", "A picture bitrate instead of constant quality: 8M, 2500k")] string? Bitrate = null,
    [property: Option("encoder", "The encoders to try, in order, comma separated")] EquatableArray<string> Encoders = default,
    [property: Option("audio-encoder", "The sound encoder: aac, libopus, flac, eac3")] string? AudioEncoder = null,
    [property: Option("audio-bitrate", "The sound bitrate: 320k")] string? AudioBitrate = null,
    [property: Option("channels", "1, 2 or 6 channels")] int? Channels = null,
    [property: Option("loudness", "Normalise the mix to this many LUFS, for example -14")] double? Loudness = null,
    [property: Option("target-size", "Come in under this size: 8MB")] string? TargetSize = null,
    [property: Option("pixel-format", "yuv420p10le for ten bits, yuv422p10le for 4:2:2 ten bit")] string? PixelFormat = null,
    [property: Option("start", "Export from here")] Flicks? Start = null,
    [property: Option("end", "Export to here")] Flicks? End = null,
    [property: Option("priority", "low, normal or high")] ExportPriority Priority = ExportPriority.Normal,
    [property: Option("open-folder", "Show the file in Explorer when it is done")] bool OpenFolder = false,
    [property: Option("run", "A script to run when it is done, given the file's path")] string? Run = null,
    [property: Option("id", "The id for the new job")] string? JobId = null) : ICommand
{
    /// <summary>The export this asks for.</summary>
    public ExportRequest ToRequest() => new(
        Output,
        Preset,
        Mode,
        SequenceId,
        SnapToKeyframes,
        UseInOut,
        External,
        Subtitles,
        SidecarFormat,
        Chapters,
        ExportOverrideText.Parse(Size, FrameRate, Quality, Bitrate, Encoders, AudioEncoder, AudioBitrate, Channels, Loudness, TargetSize, PixelFormat),
        ExportOverrideText.Range(Start, End));

    /// <summary>How the queue treats the job.</summary>
    public ExportJobOptions ToOptions() => new(Priority, OpenFolder, string.IsNullOrWhiteSpace(Run) ? null : Run);
}
