using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Reads a file and says what is in it, without importing it.</summary>
/// <remarks>
/// The one query that touches a file rather than the project, which is what makes it useful
/// before deciding whether to import something.
///
/// The conform settings are here because a warning depends on them: an interlaced file that will
/// be deinterlaced and one that will not are two different things to be told. They default to
/// what an import would choose, so asking without them says what importing would do.
/// </remarks>
/// <param name="Path">The file to read.</param>
/// <param name="Conform">How the picture would be fitted to a frame of a different shape.</param>
/// <param name="Deinterlace">Whether it would be deinterlaced on decode.</param>
/// <param name="VfrConform">Whether variable frame timing would be remapped onto the project grid.</param>
[Query("media.probe", Description = "Read a file and say what is in it, without importing it")]
public sealed record ProbeMediaQuery(
    [property: Arg(0, "The file to read")] string Path,
    [property: Option("conform", "fit, fill, stretch or native")] ConformPolicy Conform = ConformPolicy.Fit,
    [property: Option("deinterlace", "auto, on or off")] AutoSetting Deinterlace = AutoSetting.Auto,
    [property: Option("vfr-conform", "auto, on or off")] AutoSetting VfrConform = AutoSetting.Auto)
    : IQuery<MediaProbeInfo>;
