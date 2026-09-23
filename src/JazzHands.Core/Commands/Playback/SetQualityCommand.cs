namespace JazzHands.Core.Commands;

/// <summary>Chooses how much of the frame the preview renders.</summary>
/// <param name="Quality">Full, half, quarter, or auto.</param>
[Command("playback.set-quality",
    Description = "Choose the preview resolution",
    Undoable = false,
    NotUndoableReason = "Preview quality is a setting of the editor, not of the project.")]
public sealed record SetQualityCommand(
    [property: Arg(0, "full, half, quarter or auto")] PreviewQuality Quality) : ICommand;
