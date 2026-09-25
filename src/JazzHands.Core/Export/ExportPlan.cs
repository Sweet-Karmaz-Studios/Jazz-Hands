using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Export;

/// <summary>How an export makes its file.</summary>
public enum ExportMode
{
    /// <summary>Copy when the timeline allows it and the preset agrees; encode otherwise. Only ever asked for, never planned.</summary>
    Auto,

    /// <summary>Copy the source's packets without decoding: lossless and fast, cut on keyframes.</summary>
    Copy,

    /// <summary>Render every frame through the compositor and encode it.</summary>
    Encode,
}

/// <summary>What an export does with the sequence's subtitle tracks.</summary>
public enum SubtitleDelivery
{
    /// <summary>A subtitle stream in the file per track, which players can turn on and off.</summary>
    Soft,

    /// <summary>Drawn into the picture, where everyone sees them.</summary>
    Burn,

    /// <summary>A subtitle file per track beside the video, named for its language.</summary>
    Sidecar,

    /// <summary>Left out.</summary>
    None,
}

/// <summary>
/// What someone asked to export, before the planner has decided how.
/// </summary>
/// <param name="OutputPath">Where the file goes. Relative paths are relative to the project, or the working folder for an unsaved one.</param>
/// <param name="Preset">A preset name from <see cref="ExportPresets"/>.</param>
/// <param name="Mode">Copy, encode, or let the planner choose.</param>
/// <param name="SequenceId">The sequence, or null for the active one.</param>
/// <param name="SnapToKeyframes">For a copy, move each cut to the nearest keyframe instead of refusing.</param>
/// <param name="UseInOut">Export only between the sequence's in and out points.</param>
/// <param name="External">Encode through ffmpeg.exe instead of in process, to tell an encoder bug from ours.</param>
/// <param name="Subtitles">What happens to subtitle tracks: streams in the file, burned in, files beside it, or nothing.</param>
/// <param name="SidecarFormat">The format of subtitle files written beside the video.</param>
/// <param name="Chapters">Write the sequence's chapter marks into the file.</param>
public sealed record ExportRequest(
    string OutputPath,
    string Preset = ExportPresets.Default,
    ExportMode Mode = ExportMode.Auto,
    string? SequenceId = null,
    bool SnapToKeyframes = false,
    bool UseInOut = false,
    bool External = false,
    SubtitleDelivery Subtitles = SubtitleDelivery.Soft,
    Subtitles.SubtitleFormat SidecarFormat = Subtitles.SubtitleFormat.Srt,
    bool Chapters = true);

/// <summary>The subtitles an export carries, and how.</summary>
/// <param name="Delivery">Streams, burned in, or files beside the video.</param>
/// <param name="Tracks">Each subtitle track exported, bottom track first.</param>
/// <param name="SidecarFormat">The format of files beside the video.</param>
public sealed record ExportSubtitles(
    SubtitleDelivery Delivery,
    EquatableArray<ExportSubtitleTrack> Tracks,
    Subtitles.SubtitleFormat SidecarFormat = Subtitles.SubtitleFormat.Srt) : IEquatable<ExportSubtitles>;

/// <summary>One subtitle track in an export.</summary>
/// <param name="TrackId">The track.</param>
/// <param name="Name">What players call it.</param>
/// <param name="Language">Its language, or null.</param>
/// <param name="Codec">For a stream, the encoder: mov_text, subrip or ass.</param>
/// <param name="SidecarPath">For a file beside the video, where it goes.</param>
/// <param name="Default">Shown unless the viewer turns it off.</param>
public sealed record ExportSubtitleTrack(
    string TrackId,
    string Name,
    string? Language,
    string? Codec = null,
    string? SidecarPath = null,
    bool Default = false) : IEquatable<ExportSubtitleTrack>;

/// <summary>A chapter in an export, at its time in the output.</summary>
/// <param name="Start">Where it starts in the file.</param>
/// <param name="End">Where it ends.</param>
/// <param name="Title">Its title.</param>
public sealed record ExportChapter(Flicks Start, Flicks End, string Title) : IEquatable<ExportChapter>;

/// <summary>
/// A fully resolved export: everything the exporter needs, nothing it has to decide.
/// </summary>
/// <remarks>
/// Printed by <c>jazz export --dry-run</c> and <c>export.plan</c>, stored with a queued job, and
/// read by both the in-process exporter and the ffmpeg.exe one. <see cref="Reasons"/> says why the
/// planner chose what it chose, in sentences, because "why did this re-encode?" is the first thing
/// anyone asks.
/// </remarks>
/// <param name="SequenceId">The sequence exported.</param>
/// <param name="Preset">The preset it was planned from.</param>
/// <param name="Mode">Copy or Encode; never Auto.</param>
/// <param name="OutputPath">The full path of the file to write.</param>
/// <param name="Container">The FFmpeg muxer: mp4, matroska, mov.</param>
/// <param name="Duration">How long the file plays.</param>
/// <param name="Ranges">The stretches of the sequence, in sequence time, played back to back.</param>
/// <param name="Video">The video encode, for an encode.</param>
/// <param name="Audio">The audio encode, for an encode with sound.</param>
/// <param name="Copy">What to copy, for a copy.</param>
/// <param name="Reasons">Why the planner chose this mode.</param>
/// <param name="Snaps">Cuts a copy moved to a keyframe, one per edge that moved.</param>
/// <param name="External">Encode through ffmpeg.exe.</param>
/// <param name="Subtitles">The subtitles it carries, and how; null for none.</param>
/// <param name="Chapters">The chapters it carries, at their times in the output.</param>
public sealed record ExportPlan(
    string SequenceId,
    string Preset,
    ExportMode Mode,
    string OutputPath,
    string Container,
    Flicks Duration,
    EquatableArray<TimeRange> Ranges,
    ExportVideo? Video = null,
    ExportAudio? Audio = null,
    ExportCopy? Copy = null,
    EquatableArray<string> Reasons = default,
    EquatableArray<KeyframeSnap> Snaps = default,
    bool External = false,
    ExportSubtitles? Subtitles = null,
    EquatableArray<ExportChapter> Chapters = default) : IEquatable<ExportPlan>;

/// <summary>The video side of an encode.</summary>
/// <param name="Codec">h264 or hevc.</param>
/// <param name="Encoders">FFmpeg encoders to try in order.</param>
/// <param name="Width">Output width.</param>
/// <param name="Height">Output height.</param>
/// <param name="FrameRate">Output rate: the sequence's.</param>
/// <param name="Quality">Constant quality, CRF or CQ.</param>
/// <param name="Bitrate">A bitrate target instead, or 0.</param>
/// <param name="Speed">fast, medium or slow.</param>
/// <param name="GopLength">Frames between keyframes.</param>
/// <param name="BFrames">B-frames between references.</param>
/// <param name="Lossless">Encode without loss.</param>
public sealed record ExportVideo(
    string Codec,
    EquatableArray<string> Encoders,
    int Width,
    int Height,
    Rational FrameRate,
    int Quality,
    long Bitrate,
    string Speed,
    int GopLength,
    int BFrames,
    bool Lossless) : IEquatable<ExportVideo>;

/// <summary>The sound side of an encode: the mix, as one stream.</summary>
/// <param name="Encoder">aac or flac.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="Bitrate">Bits per second, for AAC.</param>
public sealed record ExportAudio(string Encoder, int SampleRate, int Channels, long Bitrate) : IEquatable<ExportAudio>;

/// <summary>The streams and stretches of one file a copy takes.</summary>
/// <param name="MediaId">The file.</param>
/// <param name="SourcePath">Its full path when the plan was made.</param>
/// <param name="VideoStream">The picture stream, or -1.</param>
/// <param name="AudioStreams">The sound streams, in track order; a muted lane is left out.</param>
/// <param name="SourceRanges">Stretches of the file in source time, each starting on a keyframe.</param>
/// <param name="FrameRate">The picture's constant rate, so timestamps land on whole frames.</param>
/// <param name="StreamNames">What each sound stream is called, in the same order, for people reading the plan.</param>
public sealed record ExportCopy(
    string MediaId,
    string SourcePath,
    int VideoStream,
    EquatableArray<int> AudioStreams,
    EquatableArray<TimeRange> SourceRanges,
    Rational? FrameRate = null,
    EquatableArray<string> StreamNames = default) : IEquatable<ExportCopy>;

/// <summary>A cut a copy moved onto a keyframe.</summary>
/// <param name="Edge">start or end.</param>
/// <param name="Requested">Where the cut was, in source time.</param>
/// <param name="Snapped">The keyframe it moved to.</param>
/// <param name="Frame">That keyframe's frame number in the source.</param>
public sealed record KeyframeSnap(string Edge, Flicks Requested, Flicks Snapped, long Frame) : IEquatable<KeyframeSnap>;
