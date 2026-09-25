namespace JazzHands.Engine.Hosting;

/// <summary>
/// The editor's window and process, as the <c>app</c> commands reach them. The WPF application
/// registers one; a headless session has none and the commands refuse with <c>no-app</c>.
/// </summary>
/// <remarks>
/// Called on the engine's dispatcher thread. Nothing here may wait for the UI thread, which may be
/// waiting for the engine: each call checks what it can at once, posts the rest to the UI and
/// returns.
/// </remarks>
public interface IAppController
{
    /// <summary>Shows the window, restored where it was, and brings it to the front.</summary>
    void Show();

    /// <summary>Hides the window to the notification area.</summary>
    void Hide();

    /// <summary>
    /// Starts quitting, or refuses with a coded <see cref="Core.Commands.CommandException"/>:
    /// <c>unsaved-changes</c>, <c>exports-running</c>.
    /// </summary>
    /// <param name="force">Quit whatever is unsaved or running.</param>
    /// <param name="waitForExports">Let the exports finish first.</param>
    void Quit(bool force, bool waitForExports);
}
