using JazzHands.Core.Time;

namespace JazzHands.Media.Probe;

/// <summary>What kind of stream a <see cref="StreamInfo"/> describes.</summary>
public enum StreamKind
{
    /// <summary>Anything FFmpeg reports that Jazz Hands has no use for.</summary>
    Unknown,

    /// <summary>A video stream.</summary>
    Video,

    /// <summary>An audio stream.</summary>
    Audio,

    /// <summary>A subtitle stream, text or bitmap.</summary>
    Subtitle,

    /// <summary>Attached cover art or a font.</summary>
    Attachment,

    /// <summary>Timecode or other data.</summary>
    Data,
}

/// <summary>How a file's frame timing behaves, which decides whether it needs conforming on import.</summary>
public enum FrameRateMode
{
    /// <summary>Not determined, usually because the stream is not video.</summary>
    Unknown,

    /// <summary>Constant frame rate: every presentation timestamp delta is the same.</summary>
    Constant,

    /// <summary>Variable frame rate. Conform to CFR on import.</summary>
    Variable,
}

/// <summary>
/// Colour signalling read from the container and bitstream. Wrong colour handling is the most
/// common way an editor makes footage look subtly wrong, so this is carried explicitly rather
/// than guessed at render time.
/// </summary>
/// <param name="Primaries">Colour primaries, for example bt709 or bt2020.</param>
/// <param name="Transfer">Transfer characteristics, for example bt709, smpte2084 (PQ) or arib-std-b67 (HLG).</param>
/// <param name="Matrix">YUV to RGB matrix coefficients.</param>
/// <param name="IsFullRange">True for full-range (0-255) rather than limited-range (16-235) luma.</param>
/// <param name="ChromaLocation">Where chroma samples sit relative to luma.</param>
public sealed record ColorInfo(
    string Primaries,
    string Transfer,
    string Matrix,
    bool IsFullRange,
    string ChromaLocation)
{
    /// <summary>The safe default for HD material when a file says nothing.</summary>
    public static readonly ColorInfo Bt709 = new("bt709", "bt709", "bt709", false, "left");

    /// <summary>True when the transfer function is PQ or HLG, so the compositor must tone map.</summary>
    public bool IsHdr =>
        Transfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) ||
        Transfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// HDR mastering display and content light level metadata, when the file carries it.
/// </summary>
/// <param name="MinLuminanceNits">Mastering display minimum luminance.</param>
/// <param name="MaxLuminanceNits">Mastering display maximum luminance.</param>
/// <param name="MaxContentLightLevel">MaxCLL, in nits.</param>
/// <param name="MaxFrameAverageLightLevel">MaxFALL, in nits.</param>
public sealed record HdrMetadata(
    double MinLuminanceNits,
    double MaxLuminanceNits,
    int MaxContentLightLevel,
    int MaxFrameAverageLightLevel);

/// <summary>One stream inside a container.</summary>
/// <param name="Index">The stream index, which is what clips reference.</param>
/// <param name="Kind">Video, audio, subtitle and so on.</param>
/// <param name="CodecName">The short codec name, for example h264, hevc, aac.</param>
/// <param name="CodecLongName">The human-readable codec name.</param>
/// <param name="Profile">The codec profile, when the decoder reports one.</param>
/// <param name="Duration">Stream duration, or zero when unknown.</param>
/// <param name="StartTime">Presentation time of the first frame.</param>
/// <param name="TimeBase">The stream time base, kept for packet-level work.</param>
/// <param name="BitRate">Bits per second, or zero when unknown.</param>
/// <param name="IsDefault">The container's default-stream disposition.</param>
/// <param name="Language">The language tag, when tagged.</param>
/// <param name="Title">The title tag, which is how OBS labels its audio tracks.</param>
/// <param name="Tags">Every metadata tag on the stream.</param>
/// <param name="Video">Video detail, when this is a video stream.</param>
/// <param name="Audio">Audio detail, when this is an audio stream.</param>
public sealed record StreamInfo(
    int Index,
    StreamKind Kind,
    string CodecName,
    string CodecLongName,
    string? Profile,
    Flicks Duration,
    Flicks StartTime,
    Rational TimeBase,
    long BitRate,
    bool IsDefault,
    string? Language,
    string? Title,
    IReadOnlyDictionary<string, string> Tags,
    VideoStreamInfo? Video = null,
    AudioStreamInfo? Audio = null);

/// <summary>Video detail for a <see cref="StreamInfo"/>.</summary>
/// <param name="Width">Coded width in pixels.</param>
/// <param name="Height">Coded height in pixels.</param>
/// <param name="PixelFormat">The FFmpeg pixel format name, for example yuv420p10le.</param>
/// <param name="BitDepth">Bits per colour component.</param>
/// <param name="FrameRate">The nominal frame rate as an exact rational.</param>
/// <param name="AverageFrameRate">The average frame rate FFmpeg computed, which differs for VFR.</param>
/// <param name="SampleAspectRatio">Pixel aspect ratio, 1/1 for square pixels.</param>
/// <param name="FrameCount">Frames reported by the container, or zero when it does not say.</param>
/// <param name="IsInterlaced">True when the stream is flagged interlaced.</param>
/// <param name="FrameRateMode">Constant or variable, from a packet timestamp scan.</param>
/// <param name="Color">Colour signalling.</param>
/// <param name="Hdr">HDR mastering metadata, when present.</param>
/// <param name="HasAlpha">True when the pixel format carries an alpha channel.</param>
public sealed record VideoStreamInfo(
    int Width,
    int Height,
    string PixelFormat,
    int BitDepth,
    Rational FrameRate,
    Rational AverageFrameRate,
    Rational SampleAspectRatio,
    long FrameCount,
    bool IsInterlaced,
    FrameRateMode FrameRateMode,
    ColorInfo Color,
    HdrMetadata? Hdr,
    bool HasAlpha)
{
    /// <summary>Display width, once the pixel aspect ratio is applied.</summary>
    public int DisplayWidth => SampleAspectRatio.Num == SampleAspectRatio.Den
        ? Width
        : (int)Math.Round(Width * SampleAspectRatio.ToDouble());
}

/// <summary>Audio detail for a <see cref="StreamInfo"/>.</summary>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="ChannelLayout">The layout name, for example stereo or 5.1.</param>
/// <param name="SampleFormat">The FFmpeg sample format name.</param>
/// <param name="BitsPerSample">Bits per sample, zero for compressed formats.</param>
public sealed record AudioStreamInfo(
    int SampleRate,
    int Channels,
    string ChannelLayout,
    string SampleFormat,
    int BitsPerSample);

/// <summary>A chapter marker in the container.</summary>
/// <param name="Id">The chapter id FFmpeg assigned.</param>
/// <param name="Start">Chapter start.</param>
/// <param name="End">Chapter end.</param>
/// <param name="Title">The chapter title, when tagged.</param>
public sealed record ChapterInfo(long Id, Flicks Start, Flicks End, string? Title);

/// <summary>
/// Everything Jazz Hands knows about a media file before it decodes a frame of it. Produced by
/// <see cref="Prober"/>, stored on the media item in the project, and the input to conform
/// decisions, decoder selection and the compositor's colour handling.
/// </summary>
/// <param name="Path">The file that was probed.</param>
/// <param name="FormatName">The demuxer short name, for example mov,mp4,m4a,3gp,3g2,mj2.</param>
/// <param name="FormatLongName">The human-readable container name.</param>
/// <param name="Duration">Container duration.</param>
/// <param name="StartTime">Container start time.</param>
/// <param name="BitRate">Overall bits per second.</param>
/// <param name="SizeBytes">File size on disk.</param>
/// <param name="Streams">Every stream, in container order.</param>
/// <param name="Chapters">Container chapters.</param>
/// <param name="Tags">Container-level metadata.</param>
public sealed record MediaProbe(
    string Path,
    string FormatName,
    string FormatLongName,
    Flicks Duration,
    Flicks StartTime,
    long BitRate,
    long SizeBytes,
    IReadOnlyList<StreamInfo> Streams,
    IReadOnlyList<ChapterInfo> Chapters,
    IReadOnlyDictionary<string, string> Tags)
{
    /// <summary>The video streams, in container order.</summary>
    public IEnumerable<StreamInfo> VideoStreams => Streams.Where(stream => stream.Kind == StreamKind.Video);

    /// <summary>The audio streams, in container order. OBS recordings have several.</summary>
    public IEnumerable<StreamInfo> AudioStreams => Streams.Where(stream => stream.Kind == StreamKind.Audio);

    /// <summary>The subtitle streams, in container order.</summary>
    public IEnumerable<StreamInfo> SubtitleStreams => Streams.Where(stream => stream.Kind == StreamKind.Subtitle);

    /// <summary>The stream a clip gets by default: the first video stream, else the first audio stream.</summary>
    public StreamInfo? PrimaryStream =>
        VideoStreams.FirstOrDefault(stream => stream.IsDefault)
        ?? VideoStreams.FirstOrDefault()
        ?? AudioStreams.FirstOrDefault(stream => stream.IsDefault)
        ?? AudioStreams.FirstOrDefault();

    /// <summary>True when any video stream carries an HDR transfer function.</summary>
    public bool IsHdr => VideoStreams.Any(stream => stream.Video?.Color.IsHdr == true);

    /// <summary>True when any video stream has variable frame timing, so import must conform it.</summary>
    public bool IsVariableFrameRate =>
        VideoStreams.Any(stream => stream.Video?.FrameRateMode == FrameRateMode.Variable);
}
