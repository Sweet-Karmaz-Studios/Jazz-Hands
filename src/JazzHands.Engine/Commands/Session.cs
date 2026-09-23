using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Engine.Diagnostics;
using JazzHands.Engine.Recovery;
using Serilog;

namespace JazzHands.Engine.Commands;

/// <summary>
/// One open project, and everything that can be done to it.
/// </summary>
/// <remarks>
/// The GUI has one of these. So does a <c>jazz</c> process running a command headless, and so
/// does each connection to the control server. Everything above it, on every surface, goes
/// through <see cref="ExecuteAsync"/> and <see cref="Query{TResult}"/> and nothing else.
///
/// The session is what turns a dispatcher into an editor: it knows where the project came from,
/// whether it has been saved, what has been done to it, and it keeps the recovery copy and the
/// command log up to date so a crash costs seconds.
/// </remarks>
public sealed class Session : ISessionState, IAsyncDisposable
{
    private readonly ILogger _log = Log.ForContext<Session>();
    private readonly CommandDispatcher _dispatcher;
    private readonly AutosaveService? _autosave;
    private readonly HistoryLog? _history;
    private readonly bool _ownsRecovery;

    private long _savedVersion;
    private bool _disposed;

    /// <summary>Opens a session over a project.</summary>
    /// <param name="project">The project to start with.</param>
    /// <param name="services">Where handlers come from.</param>
    /// <param name="path">Where the project lives, or empty when it has never been saved.</param>
    /// <param name="clock">The clock, injected for tests.</param>
    /// <param name="recovery">
    /// False in tests and short-lived headless runs, which have nothing to recover and should not
    /// litter a sidecar folder beside the project.
    /// </param>
    /// <param name="undoLimit">
    /// How many commands to remember. A thousand is far more than anyone undoes through and
    /// bounds what a long session holds; the tests that prove undo is exact ask for more.
    /// </param>
    public Session(
        Project project,
        IServiceProvider services,
        string path = "",
        TimeProvider? clock = null,
        bool recovery = false,
        int undoLimit = 1000)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(services);

        _dispatcher = new CommandDispatcher(project, services, clock, undoLimit) { ProjectPath = path };
        _dispatcher.ProjectChanged += OnProjectChanged;

        ProjectPath = path;
        _savedVersion = _dispatcher.Version;

        _ownsRecovery = recovery && path.Length > 0;

        if (_ownsRecovery)
        {
            _autosave = new AutosaveService(path, clock);
            _autosave.Watch(() => _dispatcher.Project);
            _history = new HistoryLog(path);
        }
    }

    /// <summary>Raised after every change.</summary>
    public event EventHandler<ProjectChangedEventArgs>? ProjectChanged;

    /// <summary>The project as it now is.</summary>
    public Project Project => _dispatcher.Project;

    /// <summary>Where the project lives, or empty when it has never been saved.</summary>
    public string ProjectPath { get; private set; }

    /// <summary>How many commands have been applied since the session opened.</summary>
    public long Version => _dispatcher.Version;

    /// <summary>True when there are changes the file does not have.</summary>
    public bool IsDirty => Version != Interlocked.Read(ref _savedVersion);

    /// <summary>The undo history.</summary>
    public UndoStack Undo => _dispatcher.Undo;

    /// <summary>Runs a command.</summary>
    /// <remarks>
    /// Commands that replace or save the project are caught here rather than in the dispatcher,
    /// because they are about the session rather than about the project: which file is open, and
    /// whether what is open matches what is on disk.
    /// </remarks>
    public async Task<CommandResult> ExecuteAsync(ICommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        switch (command)
        {
            case NewProjectCommand or OpenProjectCommand:
                return Replace(command);

            case SaveProjectCommand save:
                return Save(save);

            default:
                CommandResult result = await _dispatcher.ExecuteAsync(command, cancellationToken)
                    .ConfigureAwait(false);

                // Only what changes the project goes in the log. It is replayed after a crash, and
                // replaying undo, redo or a press of play would do something other than rebuild
                // the edit.
                if (result.Ok && _history is not null && CommandRegistry.Describe(command).Undoable)
                {
                    _history.Append(CommandRegistry.NameOf(command), CommandRegistry.ArgsToJson(command));
                }

                return result;
        }
    }

    /// <summary>Runs several commands as one undo step.</summary>
    public Task<CommandResult> ExecuteAsync(
        IReadOnlyList<ICommand> commands,
        string label = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);

        return ExecuteAsync(new BatchCommand([.. commands], label), cancellationToken);
    }

    /// <summary>Answers a query against the project as it is now.</summary>
    public TResult Query<TResult>(IQuery<TResult> query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.Query(query, this);
    }

    /// <summary>What has been done, oldest first.</summary>
    public ImmutableArray<HistoryInfo> History(int limit) => Undo.History(limit);

    /// <summary>
    /// What this session has noticed that a person should know about.
    /// </summary>
    /// <remarks>
    /// The decode pipeline reports into this: a file that fell back to software, a file that is
    /// not where the project says it is. Every surface reads the same list, which is why it is on
    /// the session and not on whichever panel happened to notice.
    /// </remarks>
    public DiagnosticsLog Notices { get; } = new();

    /// <inheritdoc />
    ImmutableArray<Core.Diagnostics.Diagnostic> ISessionState.Diagnostics => Notices.All;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dispatcher.ProjectChanged -= OnProjectChanged;

        await _dispatcher.DisposeAsync().ConfigureAwait(false);

        _autosave?.Dispose();
        _history?.Dispose();
    }

    /// <summary>
    /// Runs a command that produces a whole new project, and adopts it.
    /// </summary>
    /// <remarks>
    /// Refused while there are unsaved changes, unless the command says to discard them. Losing
    /// an afternoon to a mistyped <c>jazz project open</c> is exactly the kind of thing an editor
    /// has no excuse for.
    /// </remarks>
    private CommandResult Replace(ICommand command)
    {
        bool discard = command switch
        {
            NewProjectCommand create => create.Discard,
            OpenProjectCommand open => open.Discard,
            _ => false,
        };

        if (IsDirty && !discard)
        {
            throw new CommandException(
                "unsaved-changes",
                "This project has unsaved changes. Save it, or pass --discard to throw them away.");
        }

        var context = new HandlerContext(null, TimeProvider.System, ProjectPath);
        Project replacement = command switch
        {
            NewProjectCommand create => new Handlers.NewProjectHandler().Handle(Project, create, context),
            OpenProjectCommand open => new Handlers.OpenProjectHandler().Handle(Project, open, context),
            _ => throw new CommandException("not-a-replacement", "That command does not replace the project."),
        };

        string path = command is OpenProjectCommand opened ? System.IO.Path.GetFullPath(opened.Path) : string.Empty;

        ProjectPath = path;
        _dispatcher.Load(replacement, path);
        Interlocked.Exchange(ref _savedVersion, _dispatcher.Version);

        _autosave?.Clear();
        _history?.Clear();

        _log.Information("Session now holds {Project}", path.Length > 0 ? path : replacement.Name);
        return CommandResult.Success(_dispatcher.Version, context.ChangedIds);
    }

    /// <summary>Writes the project and marks the session clean.</summary>
    private CommandResult Save(SaveProjectCommand command)
    {
        var context = new HandlerContext(null, TimeProvider.System, ProjectPath);
        Project written = new Handlers.SaveProjectHandler().Handle(Project, command, context);

        ProjectPath = System.IO.Path.GetFullPath(command.Path ?? ProjectPath);
        _dispatcher.ProjectPath = ProjectPath;

        // Saving can change the project, because an absolute media path becomes a relative one.
        // That is not an edit, so it does not go on the undo stack, but the session has to hold
        // what was actually written or the next save would write something different again.
        if (written != Project)
        {
            _dispatcher.Load(written, ProjectPath);
            _dispatcher.Undo.Clear();
        }

        Interlocked.Exchange(ref _savedVersion, _dispatcher.Version);

        _autosave?.Clear();
        _history?.Clear();

        return CommandResult.Success(_dispatcher.Version, context.ChangedIds);
    }

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs args)
    {
        _autosave?.OnCommand();

        // Each subscriber is called on its own. A panel with a bug in its redraw must not stop
        // the other panels hearing about a change that has already happened.
        foreach (Delegate subscriber in ProjectChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<ProjectChangedEventArgs>)subscriber)(this, args);
            }
            catch (Exception error)
            {
                _log.Error(error, "A ProjectChanged subscriber threw");
            }
        }
    }
}
