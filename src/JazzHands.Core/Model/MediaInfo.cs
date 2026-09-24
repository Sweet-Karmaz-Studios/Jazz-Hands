using JazzHands.Core.Time;

namespace JazzHands.Core.Model;

/// <summary>What a media item is.</summary>
public enum MediaKind
{
    /// <summary>A file with moving pictures, sound, or both.</summary>
    Movie,

    /// <summary>A single image.</summary>
    Still,

    /// <summary>Numbered image files played as frames.</summary>
    ImageSequence,
}

/// <summary>What a stream carries.</summary>
public enum MediaStreamKind
{
    /// <summary>Picture.</summary>
    Video,

    /// <summary>Sound.</summary>
    Audio,

    /// <summary>Captions.</summary>
    Subtitle,

    /// <summary>Timed data, such as timecode.</summary>
    Data,

    /// <summary>An embedded file, such as a font.</summary>
    Attachment,
}

/// <summary>How a clip's picture is fitted to a frame that is a different shape.</summary>
public enum ConformPolicy
{
    /// <summary>Scale until it fits inside the frame, letterboxing or pillarboxing the rest.</summary>
    Fit,

    /// <summary>Scale until it covers the frame, cropping what hangs over.</summary>
    Fill,

    /// <summary>Scale each axis independently to fill the frame exactly. Distorts.</summary>
    Stretch,

    /// <summary>Place it at its own size, centred.</summary>
    Native,
}

/// <summary>A setting that can defer to what the file says.</summary>
/// <remarks>
/// Import decides most of these from the probe, and a person can override either way afterwards.
/// Keeping the three states apart matters: "off because the file is progressive" and "off because
/// the user said so" look the same until the file is replaced.
/// </remarks>
public enum AutoSetting
{
    /// <summary>Decide from what the file says.</summary>
    Auto,

    /// <summary>Always.</summary>
    On,

    /// <summary>Never.</summary>
    Off,
}

/// <summary>
/// One stream of a media file, as the project remembers it.
/// </summary>
/// <remarks>
/// A flattened summary of what the prober found, not the prober's own record. Core cannot
/// reference the media layer, and should not: a project file has to be readable and checkable
/// without FFmpeg anywhere near it, which is the rule <c>CoreIsolationTests</c> enforces. The
/// media layer maps its <c>MediaProbe</c> onto this on import.
/// </remarks>
/// <param name="Index">The stream index in the container.</param>
/// <param name="Kind">What it carries.</param>
/// <param name="Codec">The codec short name, for example hevc.</param>
/// <param name="Duration">How long the stream runs.</param>
/// <param name="Language">The language tag, when there is one.</param>
/// <param name="Title">The stream title, which is how OBS names its audio tracks.</param>
/// <param name="Width">Picture width in pixels, zero for a stream with no picture.</param>
/// <param name="Height">Picture height in pixels.</param>
/// <param name="FrameRate">The nominal frame rate, exactly. Null for a stream with no picture.</param>
/// <param name="BitDepth">Bits per colour component.</param>
/// <param name="IsInterlaced">True when the container flags the stream interlaced.</param>
/// <param name="IsVariableFrameRate">True when its frames do not sit on a fixed grid.</param>
/// <param name="IsHdr">True when it carries an HDR transfer function.</param>
/// <param name="HasAlpha">True when its pixel format carries an alpha channel.</param>
/// <param name="SampleRate">Audio samples per second, zero for a stream with no sound.</param>
/// <param name="Channels">Audio channel count.</param>
/// <param name="ChannelLayout">The layout name, for example stereo.</param>
/// <param name="Color">The colour signalling of a picture stream, as the file states it. Null for sound, and for projects saved before Phase 17.</param>
public sealed record MediaStream(
    int Index,
    MediaStreamKind Kind,
    string Codec,
    Flicks Duration,
    string? Language = null,
    string? Title = null,
    int Width = 0,
    int Height = 0,
    Rational? FrameRate = null,
    int BitDepth = 0,
    bool IsInterlaced = false,
    bool IsVariableFrameRate = false,
    bool IsHdr = false,
    bool HasAlpha = false,
    int SampleRate = 0,
    int Channels = 0,
    string ChannelLayout = "",
    StreamColor? Color = null) : IEquatable<MediaStream>
{
    /// <summary>A short label for the media panel: "1920x1080 hevc" or "stereo 48 kHz aac".</summary>
    public string Describe() => Kind switch
    {
        MediaStreamKind.Video => $"{Width}x{Height} {Codec}",
        MediaStreamKind.Audio => $"{ChannelLayout} {SampleRate} Hz {Codec}",
        _ => $"{Kind.ToString().ToLowerInvariant()} {Codec}",
    };
}

/// <summary>
/// The cached result of probing a file, kept on the media item.
/// </summary>
/// <remarks>
/// Stored in the project so that opening one does not have to touch every file it references:
/// a project on a disconnected drive still shows its bin, its durations and its stream lists.
/// Regenerated when the content hash changes, which is how a replaced file is noticed.
/// </remarks>
/// <param name="FormatName">The container short name.</param>
/// <param name="Duration">Container duration.</param>
/// <param name="SizeBytes">File size at probe time.</param>
/// <param name="BitRate">Overall bits per second.</param>
/// <param name="Streams">Every stream, in container order.</param>
/// <param name="ProbedAt">When it was probed.</param>
public sealed record MediaInfo(
    string FormatName,
    Flicks Duration,
    long SizeBytes,
    long BitRate,
    EquatableArray<MediaStream> Streams = default,
    DateTimeOffset ProbedAt = default) : IEquatable<MediaInfo>
{
    /// <summary>The video streams, in container order.</summary>
    public IEnumerable<MediaStream> VideoStreams => Streams.Where(stream => stream.Kind == MediaStreamKind.Video);

    /// <summary>The audio streams, in container order.</summary>
    public IEnumerable<MediaStream> AudioStreams => Streams.Where(stream => stream.Kind == MediaStreamKind.Audio);

    /// <summary>The first video stream, or the first audio stream when there is no picture.</summary>
    public MediaStream? PrimaryStream => VideoStreams.FirstOrDefault() ?? AudioStreams.FirstOrDefault();

    /// <summary>True when any video stream has variable frame timing, so import must conform it.</summary>
    public bool IsVariableFrameRate => VideoStreams.Any(stream => stream.IsVariableFrameRate);

    /// <summary>True when any video stream is flagged interlaced.</summary>
    public bool IsInterlaced => VideoStreams.Any(stream => stream.IsInterlaced);

    /// <summary>True when any video stream carries an HDR transfer function.</summary>
    public bool IsHdr => VideoStreams.Any(stream => stream.IsHdr);

    /// <summary>True when the file has no sound at all.</summary>
    public bool IsSilent => !AudioStreams.Any();
}

/// <summary>
/// A run of numbered image files played as frames.
/// </summary>
/// <remarks>
/// <c>render.%04d.exr</c> from 1 to 240 is one media item, not two hundred and forty. The pattern
/// lives in the media item's path so that a sequence moves with the project like any other file.
/// </remarks>
/// <param name="Start">The first number in the run.</param>
/// <param name="Count">How many frames there are.</param>
/// <param name="FrameRate">The rate they are played at, which no image file can say.</param>
/// <param name="Padding">How many digits the numbers are padded to.</param>
public sealed record ImageSequenceInfo(
    int Start,
    int Count,
    Rational FrameRate,
    int Padding) : IEquatable<ImageSequenceInfo>
{
    /// <summary>How long the whole run lasts.</summary>
    public Flicks Duration => Flicks.FromFrames(Count, FrameRate);

    /// <summary>The file name for one frame of the run.</summary>
    public string FileName(string pattern, int index)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        int marker = pattern.IndexOf('%', StringComparison.Ordinal);
        if (marker < 0)
        {
            return pattern;
        }

        int close = pattern.IndexOf('d', marker);
        if (close < 0)
        {
            return pattern;
        }

        string number = (Start + index).ToString($"D{Padding}", System.Globalization.CultureInfo.InvariantCulture);
        return string.Concat(pattern.AsSpan(0, marker), number, pattern.AsSpan(close + 1));
    }
}
