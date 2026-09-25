using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Subtitles;
using JazzHands.Core.Time;

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
[Query("export.plan", Description = "Plan an export without running it")]
public sealed record PlanExportQuery(
    [property: Arg(0, "Where the file would go")] string Output,
    [property: Option("preset", "Which preset")] string Preset = ExportPresets.Default,
    [property: Option("mode", "auto, smart, copy, or encode")] ExportMode Mode = ExportMode.Auto,
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
    [property: Option("audio-only", "Write the sound alone, in a sound file for its encoder: .m4a, .opus, .flac, .wav or .mp3")] bool AudioOnly = false,
    [property: Option("start", "Export from here")] Flicks? Start = null,
    [property: Option("end", "Export to here")] Flicks? End = null) : IQuery<ExportPlan>
{
    /// <summary>The export this asks about.</summary>
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
        ExportOverrideText.Parse(Size, FrameRate, Quality, Bitrate, Encoders, AudioEncoder, AudioBitrate, Channels, Loudness, TargetSize, PixelFormat, AudioOnly),
        ExportOverrideText.Range(Start, End));
}
