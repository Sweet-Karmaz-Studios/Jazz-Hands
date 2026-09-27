using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Opens a media item in the source monitor.</summary>
/// <remarks>
/// The source monitor is the second viewer (Phase 38): it plays one file, not the sequence, and
/// its in and out marks say what a three-point edit takes from it. What it holds is view state,
/// kept by the session rather than the project, so it is readable and settable over JSON-RPC and
/// MCP but not undoable and not saved. Opening an item clears the marks unless it is already open.
/// </remarks>
/// <param name="MediaId">The media item.</param>
/// <param name="At">Where to put its playhead, in source time; its in point (a subclip's) when left out.</param>
[Command("source.open",
    Description = "Open a media item in the source monitor",
    Undoable = false,
    NotUndoableReason = "The source monitor is a viewer. It does not change the project.")]
public sealed record OpenSourceCommand(
    [property: Arg(0, "The media id")] string MediaId,
    [property: Option("at", "Where to put its playhead, in source time")] Flicks? At = null) : ICommand;
