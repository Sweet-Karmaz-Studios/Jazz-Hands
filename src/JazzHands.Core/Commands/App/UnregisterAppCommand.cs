namespace JazzHands.Core.Commands;

/// <summary>
/// Takes back everything the editor told Windows about itself: the <c>.jazz</c> association, the
/// Explorer verbs on video files, the <c>jazzhands:</c> links and starting with Windows.
/// </summary>
/// <remarks>
/// The step before uninstalling. The editor writes these in its own session when it starts, where
/// the uninstaller cannot reach them, so uninstalling leaves them unless this runs first. The
/// editor keeps running with start with Windows turned off in its settings; the next time it
/// starts it registers the rest again. Refuses with
/// <c>not-registered</c> in a copy that never registers (portable, or isolated with
/// <c>JAZZ_HOME</c>).
/// </remarks>
[Command("app.unregister",
    Description = "Remove Jazz Hands from Windows (file association, Explorer verbs, start with Windows) before uninstalling",
    Undoable = false,
    NotUndoableReason = "It changes Windows, not the project; the editor registers again when it next starts.")]
public sealed record UnregisterAppCommand : ICommand;
