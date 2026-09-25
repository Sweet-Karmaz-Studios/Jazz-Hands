namespace JazzHands.Core.Commands;

/// <summary>Hides the editor's window to the notification area, where it keeps running.</summary>
/// <remarks>
/// The session, the export queue and the control server carry on; the preview lets go of the
/// graphics memory it holds until the window is shown again.
/// </remarks>
[Command("app.hide",
    Description = "Hide the editor to the notification area, where it keeps running",
    Undoable = false,
    NotUndoableReason = "It changes the window, not the project.")]
public sealed record HideAppCommand : ICommand;
