namespace JazzHands.Core.Commands;

/// <summary>Analyses how the camera moves through a video, for stabilization, and keeps the result in the project's sidecar folder.</summary>
/// <remarks>
/// <c>clip.stabilize</c> does this itself the first time; this is for doing it ahead of time, or
/// again. It reads the whole file once through vid.stab's detect pass.
/// </remarks>
/// <param name="MediaId">The media item.</param>
/// <param name="Stream">Which video stream, by its index in the file; the first video stream when left out.</param>
[Command("media.analyze-motion", Description = "Analyse a video's camera motion for stabilization",
    Undoable = false,
    NotUndoableReason = "The analysis is kept beside the project, like a cache. The project does not change.")]
public sealed record AnalyzeMotionCommand(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Option("stream", "The video stream's index in the file")] int? Stream = null) : ICommand;
