using JazzHands.App.Services;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Engine.Export;
using JazzHands.Engine.Hosting;
using JazzHands.Engine.Settings;
using Serilog;

namespace JazzHands.App.Shell;

/// <summary>The window, as the lifetime sees it; <see cref="MainWindow"/> is the real one.</summary>
public interface IAppWindow
{
    /// <summary>True while the window is showing, not minimised, and in front.</summary>
    bool IsInFront { get; }

    /// <summary>Shows the window where it was and brings it to the front.</summary>
    void ShowInFront();

    /// <summary>Hides the window; its taskbar button goes with it.</summary>
    void HideToTray();
}

/// <summary>Something that should stop working while the window is hidden: the preview, the meters.</summary>
public interface IQuietWhileHidden
{
    /// <summary>Stops (true) or starts again (false).</summary>
    void SetQuiet(bool quiet);
}

/// <summary>What to do about exports still running when the person asks to quit.</summary>
public enum QuitChoice
{
    /// <summary>Let them finish, hidden, then quit.</summary>
    Wait,

    /// <summary>Pause them, to carry on at the next start, and quit now.</summary>
    PauseForNextTime,

    /// <summary>Cancel them and quit now.</summary>
    QuitAnyway,

    /// <summary>Do not quit.</summary>
    Cancel,
}

/// <summary>
/// The editor's process as distinct from its window: hidden to the notification area, shown
/// again, and quit in order. The <c>app</c> commands, the notification area and the window's close
/// button all come here.
/// </summary>
/// <remarks>
/// <para>
/// The window is a view of the session, not the session. Closing it hides it (unless Settings say
/// to quit), and the session, the export queue and the control server carry on, so an export
/// finishes and <c>jazz --attach</c> and Claude Code keep their connection. While hidden, the
/// preview lets go of its video memory and the meters stop polling (<see cref="IQuietWhileHidden"/>).
/// The first time the window hides, one notification says Jazz Hands is still running and how to
/// quit; never again.
/// </para>
/// <para>
/// Quitting asks about unsaved work and running exports first. From the <c>app.quit</c> command,
/// which cannot ask, it refuses instead (<c>unsaved-changes</c>, <c>exports-running</c>) unless
/// forced or told to wait. The quit itself is the application's shutdown: the autosave written, the
/// control server stopped, the session and the services disposed.
/// </para>
/// </remarks>
public sealed class AppLifetime : IAppController
{
    private readonly ILogger _log = Log.ForContext<AppLifetime>();
    private readonly IUiDispatcher _ui;
    private readonly ISession _session;
    private readonly IExportService? _exports;
    private readonly SettingsSection<EditorSettings> _editor;
    private readonly IReadOnlyList<IQuietWhileHidden> _quiet;
    private readonly Action _shutdown;
    private IAppWindow? _window;
    private Action<string, string>? _notify;
    private Func<Task<bool>>? _readyToClose;
    private IDialogService? _dialogs;
    private bool _hidden;
    private bool _waitingForExports;
    private bool _quitting;

    /// <summary>A lifetime over the editor's parts.</summary>
    /// <param name="ui">The UI thread.</param>
    /// <param name="session">The session, for unsaved changes.</param>
    /// <param name="exports">The export queue, for exports still running.</param>
    /// <param name="editor">The editor's settings: close to the notification area or quit.</param>
    /// <param name="quiet">What stops while hidden.</param>
    /// <param name="shutdown">Ends the application; its exit disposes everything in order.</param>
    public AppLifetime(IUiDispatcher ui, ISession session, IExportService? exports, SettingsSection<EditorSettings> editor, IEnumerable<IQuietWhileHidden> quiet, Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(quiet);
        ArgumentNullException.ThrowIfNull(shutdown);
        _ui = ui;
        _session = session;
        _exports = exports;
        _editor = editor;
        _quiet = [.. quiet];
        _shutdown = shutdown;
        _exports?.Changed += (_, _) => _ui.Post(QuitIfExportsDone);
    }

    /// <summary>True while the window is hidden to the notification area.</summary>
    public bool IsHidden => _hidden;

    /// <summary>True once quitting has begun.</summary>
    public bool IsQuitting => _quitting;

    /// <summary>True while waiting for exports to finish before quitting.</summary>
    public bool IsWaitingForExports => _waitingForExports;

    /// <summary>Joins the lifetime to the window and the parts that talk to the person.</summary>
    /// <param name="window">The window.</param>
    /// <param name="notify">Shows a notification from the notification area: a title and a line.</param>
    /// <param name="readyToClose">Asks about unsaved changes; false when the person cancels.</param>
    /// <param name="dialogs">Asks about running exports.</param>
    public void Attach(IAppWindow window, Action<string, string>? notify = null, Func<Task<bool>>? readyToClose = null, IDialogService? dialogs = null)
    {
        _window = window;
        _notify = notify;
        _readyToClose = readyToClose;
        _dialogs = dialogs;
    }

    /// <summary>Starts hidden, as <c>--background</c> and starting with Windows do: quiet, and no notice.</summary>
    public void StartHidden()
    {
        _hidden = true;
        foreach (IQuietWhileHidden part in _quiet)
        {
            part.SetQuiet(true);
        }
    }

    /// <inheritdoc />
    public void Show() => _ui.Post(ShowNow);

    /// <inheritdoc />
    public void Hide() => _ui.Post(HideNow);

    /// <inheritdoc />
    public void Quit(bool force, bool waitForExports)
    {
        if (!force && _session.IsDirty)
        {
            throw new CommandException(
                "unsaved-changes",
                "The project has changes that are not saved. Save it (project.save), or quit with --force to lose them.");
        }

        int running = Running().Length;
        if (running > 0 && !force && !waitForExports)
        {
            throw new CommandException(
                "exports-running",
                $"{running} export(s) are still running. Quit with --wait-for-exports to let them finish first, or --force to cancel them.");
        }

        _ui.Post(() =>
        {
            if (running > 0 && waitForExports && !force)
            {
                WaitForExports();
                return;
            }

            if (force)
            {
                CancelRunning();
            }

            QuitNow();
        });
    }

    /// <summary>
    /// The window's close button. True when it was dealt with here (hidden, or a quit begun), so
    /// the window should not close itself.
    /// </summary>
    public bool CloseRequested()
    {
        if (_quitting)
        {
            return false;
        }

        if (_editor.Current.CloseToTray)
        {
            HideNow();
            return true;
        }

        _ = QuitFromPersonAsync();
        return true;
    }

    /// <summary>A left click on the notification area icon: in front, it hides; otherwise it shows.</summary>
    public void TrayClicked()
    {
        if (!_hidden && _window?.IsInFront == true)
        {
            HideNow();
        }
        else
        {
            ShowNow();
        }
    }

    /// <summary>Quit Jazz Hands from the menu: asks about exports and unsaved work, then quits.</summary>
    public async Task QuitFromPersonAsync()
    {
        if (_quitting)
        {
            return;
        }

        ExportJobInfo[] running = Running();
        if (running.Length > 0)
        {
            ShowNow();
            QuitChoice choice = _dialogs is null ? QuitChoice.QuitAnyway : await _dialogs.AskToQuitWithExportsAsync(running.Length).ConfigureAwait(true);
            switch (choice)
            {
                case QuitChoice.Cancel:
                    return;

                case QuitChoice.Wait:
                    if (_readyToClose is { } ask && !await ask().ConfigureAwait(true))
                    {
                        return;
                    }

                    WaitForExports();
                    return;

                case QuitChoice.PauseForNextTime:
                    _exports?.Pause(null);
                    break;

                case QuitChoice.QuitAnyway:
                    CancelRunning();
                    break;
            }
        }

        if (_session.IsDirty)
        {
            ShowNow();
        }

        if (_readyToClose is { } ready && !await ready().ConfigureAwait(true))
        {
            return;
        }

        QuitNow();
    }

    private void ShowNow()
    {
        if (_quitting)
        {
            return;
        }

        if (_hidden)
        {
            _hidden = false;
            foreach (IQuietWhileHidden part in _quiet)
            {
                part.SetQuiet(false);
            }
        }

        _window?.ShowInFront();
    }

    private void HideNow()
    {
        if (_hidden || _quitting)
        {
            return;
        }

        _hidden = true;
        _window?.HideToTray();
        foreach (IQuietWhileHidden part in _quiet)
        {
            part.SetQuiet(true);
        }

        _log.Information("The window is hidden; Jazz Hands carries on in the notification area");
        if (!_editor.Current.TrayNoticeShown)
        {
            _notify?.Invoke(
                "Jazz Hands is still running",
                "Exports and remote control carry on. Click the icon to bring the window back; right-click it to quit.");
            _editor.Update(settings => settings with { TrayNoticeShown = true });
        }
    }

    private void WaitForExports()
    {
        _waitingForExports = true;
        _log.Information("Quitting once the exports finish");
        HideNow();
        QuitIfExportsDone();
    }

    private void QuitIfExportsDone()
    {
        if (_waitingForExports && (_exports?.List() ?? []).All(job => job.IsFinished || job.State == ExportJobState.Paused))
        {
            _waitingForExports = false;
            QuitNow();
        }
    }

    private void CancelRunning()
    {
        foreach (ExportJobInfo job in Running())
        {
            _exports?.Cancel(job.Id);
        }
    }

    private ExportJobInfo[] Running() =>
        [.. (_exports?.List() ?? []).Where(job => job.State is ExportJobState.Running or ExportJobState.Queued)];

    private void QuitNow()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _log.Information("Quitting");
        _shutdown();
    }
}
