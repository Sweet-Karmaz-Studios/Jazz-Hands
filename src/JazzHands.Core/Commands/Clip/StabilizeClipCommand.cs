namespace JazzHands.Core.Commands;

/// <summary>Steadies a shaky clip: analyses its file's camera motion if that has not been done, then adds or updates its Stabilize effect.</summary>
/// <remarks>
/// The analysis (vid.stab's detect pass) reads the whole file once and is kept in the project's
/// sidecar folder, so a second clip of the same file, or a change of smoothing, does not read it
/// again. The effect is an ordinary undoable edit; the analysis is not part of the project and
/// stays when the edit is undone. Rendering moves, turns and zooms each frame back onto the smoothed
/// path, so scrubbing and export show the same steadied picture.
/// </remarks>
/// <param name="ClipId">Which clip. It must be a clip of a video file.</param>
/// <param name="Smoothing">Frames either side the camera's path is smoothed over.</param>
/// <param name="Zoom">Extra zoom, in percent, on top of the automatic zoom.</param>
/// <param name="NoAutoZoom">Do not zoom in to hide the moving edges.</param>
/// <param name="Reanalyze">Analyse the file again even if it has been.</param>
/// <param name="Off">Remove the Stabilize effect instead.</param>
[Command("clip.stabilize", Description = "Steady a shaky clip (analyses its motion the first time)")]
public sealed record StabilizeClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("smoothing", "Frames either side the camera path is smoothed over. Default: 15")] int Smoothing = 15,
    [property: Option("zoom", "Extra zoom in percent. Default: 0")] double Zoom = 0,
    [property: Option("no-auto-zoom", "Do not zoom in to hide the moving edges")] bool NoAutoZoom = false,
    [property: Option("reanalyze", "Analyse the file again")] bool Reanalyze = false,
    [property: Option("off", "Remove the stabilization instead")] bool Off = false) : ICommand;
