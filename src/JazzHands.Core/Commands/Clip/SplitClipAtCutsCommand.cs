using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Cuts a clip of an edited video back into its shots.</summary>
/// <remarks>
/// Finds the shot changes in the clip's file (see <c>media.detect-cuts</c>) and splits the clip,
/// and the clips linked to it, at each one inside it: on the first timeline frame that shows the
/// new shot, whatever the clip's speed, direction or speed curve. One undo.
/// </remarks>
/// <param name="ClipId">The clip.</param>
/// <param name="Threshold">How much of the picture has to change, 0 to 100; lower finds more.</param>
/// <param name="MinShot">The shortest shot there can be.</param>
/// <param name="Linked">Split the clips linked to it (its sound) at the same times.</param>
[Command("clip.split-at-cuts", Description = "Split a clip at the shot changes in its video")]
public sealed record SplitClipAtCutsCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("threshold", "How much has to change, 0 to 100; lower finds more. Default: 10")] double Threshold = SceneCuts.DefaultThreshold,
    [property: Option("min-shot", "The shortest shot there can be. Default: 0.5 s")] Flicks? MinShot = null,
    [property: Option("linked", "Split its linked clips too. Default: on")] bool Linked = true) : ICommand;
