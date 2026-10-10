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

    /// <summary>
    /// Copy the source's packets between the cuts and encode again only the frames from each cut
    /// to the next keyframe, with an encoder matched to the source: exact cuts, almost no loss.
    /// </summary>
    Smart,
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
/// <param name="Overrides">Changes to the preset for this export, or null to take it as it is.</param>
/// <param name="Range">Export only this stretch of the sequence, in sequence time; null for all of it.</param>
/// <param name="Stems">Also write a 24-bit WAV per role or per sound track beside the file (Phase 40).</param>
/// <param name="StemFormat">How the stems are written: 24-bit WAV, the preset's sound codec in its own file, or as more sound tracks in the file itself.</param>
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
    bool Chapters = true,
    ExportOverrides? Overrides = null,
    TimeRange? Range = null,
    StemMode Stems = StemMode.None,
    StemFormat StemFormat = StemFormat.Wav);

/// <summary>
/// Changes to a preset for one export: what the export dialog's overrides and the command line's
/// <c>--size</c>, <c>--fps</c>, <c>--bitrate</c> and the rest set. Zero or null leaves the
/// preset's own.
/// </summary>
/// <param name="MaxWidth">Fit the picture inside this width, never scaling up; 0 for the preset's.</param>
/// <param name="MaxHeight">Fit the picture inside this height; 0 for the preset's.</param>
/// <param name="FrameRate">Write at this rate instead of the sequence's.</param>
/// <param name="Quality">Constant quality instead of the preset's.</param>
/// <param name="Bitrate">A picture bitrate in bits per second instead of constant quality; 0 for the preset's.</param>
/// <param name="Encoders">The encoders to try, in order, instead of the preset's chain.</param>
/// <param name="AudioEncoder">The sound encoder instead of the preset's.</param>
/// <param name="AudioBitrate">The sound bitrate in bits per second; 0 for the preset's.</param>
/// <param name="Channels">1, 2 or 6 channels; 0 for the preset's.</param>
/// <param name="Loudness">Normalise the mix to this many LUFS.</param>
/// <param name="TargetBytes">Come in under this many bytes; 0 for the preset's target, if it has one.</param>
/// <param name="PixelFormat">The pixel format to encode, for ten bits or 4:2:2: yuv420p10le, yuv422p10le; null for the preset's.</param>
/// <param name="AudioOnly">Write the preset's sound alone, in a sound file for its encoder: AAC in .m4a, Opus in .opus, FLAC, WAV or MP3.</param>
public sealed record ExportOverrides(
    int MaxWidth = 0,
    int MaxHeight = 0,
    Rational? FrameRate = null,
    int? Quality = null,
    long Bitrate = 0,
    EquatableArray<string> Encoders = default,
    string? AudioEncoder = null,
    long AudioBitrate = 0,
    int Channels = 0,
    double? Loudness = null,
    long TargetBytes = 0,
    string? PixelFormat = null,
    bool AudioOnly = false) : IEquatable<ExportOverrides>
{
    /// <summary>True when it changes nothing.</summary>
    public bool IsEmpty => this == new ExportOverrides();
}

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
/// <param name="TargetBytes">The size the file must come in under, or 0. The exporter checks it and encodes again, smaller, when it does not.</param>
/// <param name="Estimate">About how big the file is and how long it takes, for the dialog and the dry run.</param>
/// <param name="Smart">What to copy and what to encode again, for a smart cut.</param>
/// <param name="Stems">The stems: files beside it (24-bit WAV or the preset's sound codec), or sound tracks in it.</param>
/// <param name="Label">What a person calls the job when its file name says nothing, as a proxy's does; null for the file name.</param>
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
    EquatableArray<ExportChapter> Chapters = default,
    long TargetBytes = 0,
    ExportEstimate? Estimate = null,
    ExportSmart? Smart = null,
    EquatableArray<ExportStem> Stems = default,
    string? Label = null) : IEquatable<ExportPlan>;

/// <summary>Which stems an export writes beside the mix (Phase 40).</summary>
public enum StemMode
{
    /// <summary>The mix only.</summary>
    None,

    /// <summary>A file per role: dialogue, music, effects, game.</summary>
    Roles,

    /// <summary>A file per sound track.</summary>
    Tracks,
}

/// <summary>How stems are written (Phase 40, and after it).</summary>
public enum StemFormat
{
    /// <summary>A 24-bit WAV each, beside the file.</summary>
    Wav,

    /// <summary>The preset's sound codec, each in the sound file it is usually written in (AAC in .m4a, Opus in .opus, FLAC), beside the file.</summary>
    Codec,

    /// <summary>More sound tracks in the file itself, after the mix, each named for its stem: MP4, MOV and Matroska.</summary>
    InFile,
}

/// <summary>
/// One stem: the sound of some tracks, played through the same mix as the file (every track plays,
/// so a ducker keyed on another track still ducks) but with only these reaching the master, so the
/// stems add up to the mix. Sample-aligned with the file and the same length. Loudness
/// normalisation is the mix's alone.
/// </summary>
/// <param name="Name">The role or the track.</param>
/// <param name="OutputPath">Where it is written: the file's name, a dash and the stem's; the file itself for a stem in it.</param>
/// <param name="TrackIds">The tracks whose sound it holds.</param>
/// <param name="Encoder">Its sound encoder: pcm_s24le for a WAV.</param>
/// <param name="Container">The FFmpeg format of its own file (wav, mp4, ogg, flac); the file's for a stem in it.</param>
/// <param name="Bitrate">Bits per second for a lossy encoder, or 0.</param>
/// <param name="InFile">True for a sound track in the exported file rather than a file of its own.</param>
public sealed record ExportStem(
    string Name,
    string OutputPath,
    EquatableArray<string> TrackIds,
    string Encoder = "pcm_s24le",
    string Container = "wav",
    long Bitrate = 0,
    bool InFile = false) : IEquatable<ExportStem>;

/// <summary>The video side of an encode.</summary>
/// <param name="Codec">h264, hevc, av1, vp9, prores, dnxhr, ffv1, gif or png.</param>
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
/// <param name="PixelFormat">The FFmpeg pixel format to encode, or null for the encoder's usual one.</param>
/// <param name="Profile">The codec profile, or null.</param>
/// <param name="Level">The codec level, or null.</param>
/// <param name="Hdr10">True for an ACES project rendered for HDR10: ten bit BT.2020 PQ, with its mastering display and light levels written into the stream and the file.</param>
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
    bool Lossless,
    string? PixelFormat = null,
    string? Profile = null,
    string? Level = null,
    bool Hdr10 = false) : IEquatable<ExportVideo>
{
    /// <summary>True when the encode keeps more than eight bits a sample, so the renderer hands over ten.</summary>
    public bool TenBit => PixelFormat is { } format && (format.Contains("10", StringComparison.Ordinal) || format.Contains("16", StringComparison.Ordinal));
}

/// <summary>The sound side of an encode: the mix, as one stream.</summary>
/// <param name="Encoder">aac, libopus, flac, libmp3lame, ac3, eac3, pcm_s16le or pcm_s24le.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">1, 2 or 6. The mix is made at this count, so a 5.1 sequence exported in stereo is folded down.</param>
/// <param name="Bitrate">Bits per second for a lossy encoder, or 0 for its default.</param>
/// <param name="Loudness">Integrated loudness to normalise to, in LUFS, or null.</param>
public sealed record ExportAudio(string Encoder, int SampleRate, int Channels, long Bitrate, double? Loudness = null) : IEquatable<ExportAudio>;

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

/// <summary>A smart cut: one file, its picture in pieces copied or encoded again, and its sound.</summary>
/// <param name="MediaId">The file.</param>
/// <param name="SourcePath">Its full path when the plan was made.</param>
/// <param name="VideoStream">The picture stream.</param>
/// <param name="AudioStreams">The sound streams, in track order; a muted lane is left out.</param>
/// <param name="SourceRanges">The stretches of the file the export plays, in source time.</param>
/// <param name="Segments">The picture's pieces, back to back.</param>
/// <param name="FrameRate">The picture's constant rate.</param>
/// <param name="Encoders">The matched encoders to try, in order.</param>
/// <param name="GopFrames">The source's frames between keyframes, which the matched encoder's groups follow.</param>
/// <param name="StreamNames">What each sound stream is called, in the same order.</param>
/// <param name="AtOnce">Pieces encoded at once; 0 for the cutter's own choice. The export queue sets it to the NVENC sessions it gave the job.</param>
public sealed record ExportSmart(
    string MediaId,
    string SourcePath,
    int VideoStream,
    EquatableArray<int> AudioStreams,
    EquatableArray<TimeRange> SourceRanges,
    EquatableArray<ExportSegment> Segments,
    Rational FrameRate,
    EquatableArray<string> Encoders,
    int GopFrames,
    EquatableArray<string> StreamNames = default,
    int AtOnce = 0) : IEquatable<ExportSmart>
{
    /// <summary>True when the first encoder it tries is NVENC.</summary>
    public bool OnNvenc => Encoders.Length > 0 && Encoders[0].Contains("nvenc", StringComparison.Ordinal);

    /// <summary>Pieces encoded again.</summary>
    public int EncodedPieces => Segments.Count(segment => segment.Encode);

    /// <summary>Frames encoded again, over all the pieces.</summary>
    public long EncodedFrames => Segments.Where(segment => segment.Encode).Sum(segment => segment.Duration.ToFrames(FrameRate, RoundingMode.Nearest));

    /// <summary>Frames copied, over all the pieces.</summary>
    public long CopiedFrames => Segments.Where(segment => !segment.Encode).Sum(segment => segment.Duration.ToFrames(FrameRate, RoundingMode.Nearest));
}

/// <summary>One piece of a smart cut's picture, in source time.</summary>
/// <param name="Start">The first frame shown.</param>
/// <param name="End">The first frame not shown.</param>
/// <param name="Encode">Encoded again; otherwise copied.</param>
/// <param name="From">For an encode, the keyframe decoding starts at; for a copy, the keyframe it stops reading at, or null for the end of the file.</param>
public sealed record ExportSegment(Flicks Start, Flicks End, bool Encode, Flicks? From = null) : IEquatable<ExportSegment>
{
    /// <summary>How long it plays.</summary>
    public Flicks Duration => End - Start;
}
