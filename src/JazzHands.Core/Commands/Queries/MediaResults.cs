using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What a query says about one media item.</summary>
/// <param name="Id">The media identifier.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Path">Where the project says it is.</param>
/// <param name="FullPath">Where it actually is on this machine.</param>
/// <param name="Exists">False when the file is not where the project expects it.</param>
/// <param name="Kind">A movie, a still, or a numbered image sequence.</param>
/// <param name="Duration">How long it runs.</param>
/// <param name="Folder">Where it sits in the bin.</param>
/// <param name="Tags">Its tags.</param>
/// <param name="Color">Its colour label.</param>
/// <param name="Hash">Its content hash.</param>
/// <param name="Conform">How its picture is fitted.</param>
/// <param name="Deinterlace">Whether it is deinterlaced, as set.</param>
/// <param name="VfrConform">Whether its timing is remapped, as set.</param>
/// <param name="WillDeinterlace">Whether it will actually be deinterlaced, once auto is resolved.</param>
/// <param name="WillConformFrameRate">Whether its timing will actually be remapped.</param>
/// <param name="Width">Picture width, zero when there is none.</param>
/// <param name="Height">Picture height.</param>
/// <param name="FrameRate">The nominal frame rate, or null when there is no picture.</param>
/// <param name="IsVariableFrameRate">True when the frames do not sit on a fixed grid.</param>
/// <param name="IsInterlaced">True when the picture is flagged interlaced.</param>
/// <param name="IsHdr">True when it carries an HDR transfer function.</param>
/// <param name="SizeBytes">File size at probe time.</param>
/// <param name="Streams">Every stream, in container order.</param>
/// <param name="UsedByClips">How many clips in the project play it.</param>
/// <param name="ProxyPath">Its proxy in the cache, when one has been made (found by its content hash).</param>
/// <param name="Subclip">For a subclip, which item it came from and its stretch of the file.</param>
/// <param name="Markers">Markers on the file, at source times.</param>
public sealed record MediaItemInfo(
    string Id,
    string Name,
    string Path,
    string FullPath,
    bool Exists,
    MediaKind Kind,
    Flicks Duration,
    string Folder,
    EquatableArray<string> Tags,
    string Color,
    string Hash,
    ConformPolicy Conform,
    AutoSetting Deinterlace,
    AutoSetting VfrConform,
    bool WillDeinterlace,
    bool WillConformFrameRate,
    int Width,
    int Height,
    Rational? FrameRate,
    bool IsVariableFrameRate,
    bool IsInterlaced,
    bool IsHdr,
    long SizeBytes,
    EquatableArray<MediaStream> Streams,
    int UsedByClips,
    string? ProxyPath,
    SubclipRange? Subclip = null,
    EquatableArray<Marker> Markers = default);

/// <summary>What reading a file said about it, before any decision to import it.</summary>
/// <param name="Path">The file that was read.</param>
/// <param name="Kind">What it looks like: a movie, a still, or one frame of a sequence.</param>
/// <param name="Info">What is in it.</param>
/// <param name="Hash">Its content hash.</param>
/// <param name="Warnings">What the user should know before importing it.</param>
public sealed record MediaProbeInfo(
    string Path,
    MediaKind Kind,
    MediaInfo Info,
    string Hash,
    EquatableArray<ProbeWarning> Warnings);

/// <summary>Something worth saying about a file before it is imported.</summary>
/// <param name="Code">A stable kebab-case code.</param>
/// <param name="Message">A sentence, written for somebody about to start editing.</param>
public sealed record ProbeWarning(string Code, string Message) : IEquatable<ProbeWarning>;
