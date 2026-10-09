using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Brings files into the project.</summary>
/// <remarks>
/// Paths may be files, folders or globs. A run of numbered images becomes one item rather than
/// one per frame. Each file is hashed and probed once, and what the probe found is kept on the
/// media item so that opening the project later does not have to touch the files again.
/// </remarks>
/// <param name="Paths">Files, folders or globs to import.</param>
/// <param name="Folder">Where they go in the bin, as a slash-separated path.</param>
/// <param name="Tags">Tags to apply to everything imported.</param>
/// <param name="Color">A colour label for the bin.</param>
/// <param name="Conform">How the picture is fitted to a frame of a different shape.</param>
/// <param name="Deinterlace">Whether to deinterlace on decode.</param>
/// <param name="VfrConform">Whether to remap variable frame timing onto the project grid.</param>
/// <param name="Recursive">Look inside sub-folders too.</param>
/// <param name="Fps">The rate an image sequence plays at.</param>
[Command("media.add", Description = "Import files, folders or globs into the project")]
public sealed record AddMediaCommand(
    [property: Arg(0, "Files, folders or globs: several, or one with commas between")] EquatableArray<string> Paths,
    [property: Option("folder", "Where they go in the bin")] string Folder = "",
    [property: Option("tags", "Comma-separated tags")] EquatableArray<string> Tags = default,
    [property: Option("color", "A colour label, for example blue")] string Color = "",
    [property: Option("conform", "fit, fill, stretch or native")] ConformPolicy Conform = ConformPolicy.Fit,
    [property: Option("deinterlace", "auto, on or off")] AutoSetting Deinterlace = AutoSetting.Auto,
    [property: Option("vfr-conform", "auto, on or off")] AutoSetting VfrConform = AutoSetting.Auto,
    [property: Option("recursive", "Look inside sub-folders")] bool Recursive = false,
    [property: Option("fps", "The rate an image sequence plays at")] Rational? Fps = null) : ICommand;
