using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Makes a subclip of every shot in an edited video.</summary>
/// <remarks>
/// Finds the shot changes (see <c>media.detect-cuts</c>) and makes a subclip of each shot between
/// them, named after the file and numbered, in a Media panel folder named after the file. One undo.
/// </remarks>
/// <param name="MediaId">The media item.</param>
/// <param name="Threshold">How much of the picture has to change, 0 to 100; lower finds more.</param>
/// <param name="MinShot">The shortest shot there can be.</param>
/// <param name="Folder">The folder to put them in; one named after the file, beside it, when left out.</param>
[Command("media.subclips-from-cuts", Description = "Make a subclip of every shot in an edited video")]
public sealed record SubclipsFromCutsCommand(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Option("threshold", "How much has to change, 0 to 100; lower finds more. Default: 10")] double Threshold = SceneCuts.DefaultThreshold,
    [property: Option("min-shot", "The shortest shot there can be. Default: 0.5 s")] Flicks? MinShot = null,
    [property: Option("folder", "The folder to put them in")] string? Folder = null) : ICommand;
