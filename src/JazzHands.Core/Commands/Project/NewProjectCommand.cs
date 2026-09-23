using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Replaces the session's project with a new empty one.</summary>
/// <remarks>
/// Not undoable, because undo lives inside a project and this is the boundary between two of
/// them. The session refuses it while there are unsaved changes unless told otherwise.
/// </remarks>
/// <param name="Name">The project name.</param>
/// <param name="Fps">The frame rate. Defaults to 30.</param>
/// <param name="Size">The frame size. Defaults to 1920x1080.</param>
/// <param name="Discard">Throw away unsaved changes in the current project.</param>
[Command("project.new",
    Description = "Create a new empty project with one video and one audio track",
    Undoable = false,
    NotUndoableReason = "Undo history belongs to a project, and this replaces the project.")]
public sealed record NewProjectCommand(
    [property: Arg(0, "The project name")] string Name,
    [property: Option("fps", "Frame rate, for example 30 or 30000/1001")] Rational? Fps = null,
    [property: Option("size", "Frame size, for example 1920x1080 or 4k")] FrameSize? Size = null,
    [property: Option("discard", "Throw away unsaved changes")] bool Discard = false) : ICommand;
