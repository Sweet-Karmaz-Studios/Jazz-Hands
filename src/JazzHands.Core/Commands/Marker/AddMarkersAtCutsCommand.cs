using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Marks the shot changes in an edited video.</summary>
/// <remarks>
/// Given a clip, the markers go on the clip, where it shows each change, and travel with it; given
/// a media item, they go on the file, at source times. Either way they are scene-cut markers, and
/// running it again replaces the ones it made before rather than adding to them. One undo.
/// </remarks>
/// <param name="TargetId">A clip id or a media id.</param>
/// <param name="Threshold">How much of the picture has to change, 0 to 100; lower finds more.</param>
/// <param name="MinShot">The shortest shot there can be.</param>
/// <param name="Color">The markers' colour, as a hex string or a name.</param>
[Command("marker.add-at-cuts", Description = "Mark the shot changes in a clip or a media item")]
public sealed record AddMarkersAtCutsCommand(
    [property: Arg(0, "A clip id or a media id")] string TargetId,
    [property: Option("threshold", "How much has to change, 0 to 100; lower finds more. Default: 10")] double Threshold = SceneCuts.DefaultThreshold,
    [property: Option("min-shot", "The shortest shot there can be. Default: 0.5 s")] Flicks? MinShot = null,
    [property: Option("color", "The markers' colour, as a hex string or a name")] string? Color = null) : ICommand;
