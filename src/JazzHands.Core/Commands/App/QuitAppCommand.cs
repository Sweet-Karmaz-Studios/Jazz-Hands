namespace JazzHands.Core.Commands;

/// <summary>Quits the editor: the autosave written, the server stopped, the process gone.</summary>
/// <remarks>
/// Refuses with <c>unsaved-changes</c> when the project has changes that are not saved, and with
/// <c>exports-running</c> while an export runs, unless told what to do about them. The answer
/// comes back before the editor goes, so a client hears it.
/// </remarks>
/// <param name="Force">Quit even with unsaved changes or a running export, which is cancelled.</param>
/// <param name="WaitForExports">Wait for the running and queued exports to finish, then quit.</param>
[Command("app.quit",
    Description = "Quit the editor",
    Undoable = false,
    NotUndoableReason = "The editor is gone afterwards.")]
public sealed record QuitAppCommand(
    [property: Option("force", "Quit even with unsaved changes or a running export")] bool Force = false,
    [property: Option("wait-for-exports", "Let the exports finish first, then quit")] bool WaitForExports = false) : ICommand;
