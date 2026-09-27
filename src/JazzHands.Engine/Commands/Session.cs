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
    private readonly bool _recoveryEnabled;
    private AutosaveService? _autosave;
    private readonly IServiceProvider _services;
    private readonly Lock _recentGate = new();
    private readonly Queue<RecentCommand> _recent = new();
    private HistoryLog? _history;
    private bool _recoveryPending;
    private readonly TimeProvider _clock;
    private readonly Lock _lockGate = new();

    private SessionLock? _lock;
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

        _clock = clock ?? TimeProvider.System;
        _services = services;
        _dispatcher = new CommandDispatcher(project, services, clock, undoLimit) { ProjectPath = path };
        _dispatcher.ProjectChanged += OnProjectChanged;

        // Handlers that start work finishing later (a watched folder) queue its commands here.
        _dispatcher.Later = (command, issuer) => ExecuteAsync(command, issuer);

        ProjectPath = path;
        _savedVersion = _dispatcher.Version;

        _recoveryEnabled = recovery;
        AttachRecovery(path);
    }

    /// <summary>Raised after every change.</summary>
    public event EventHandler<ProjectChangedEventArgs>? ProjectChanged;

    /// <summary>
    /// Raised after every command, whether it changed anything or was refused, with who asked and
    /// what came back: what the Command Console shows.
    /// </summary>
    public event EventHandler<CommandCompletedEventArgs>? CommandCompleted;

    /// <summary>
    /// Who a command is from when the caller does not say: <c>gui</c> in the editor, <c>cli</c>
    /// in a jazz process. The history and the change events carry it.
    /// </summary>
    public string DefaultIssuer { get; set; } = "local";

    /// <summary>Who holds the session for a batch of their own, or null; see <see cref="TryLock"/>.</summary>
    public SessionLock? Lock
    {
        get
        {
            lock (_lockGate)
            {
                return Held();
            }
        }
    }

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

    /// <summary>How many of the last commands <see cref="RecentCommands"/> keeps.</summary>
    public const int RecentLimit = 50;

    /// <summary>The last fifty commands, oldest first, whatever became of them: what a crash report carries.</summary>
    public IReadOnlyList<RecentCommand> RecentCommands
    {
        get
        {
            lock (_recentGate)
            {
                return [.. _recent];
            }
        }
    }

    /// <summary>True while a crash's recovery copy or history waits for accept or discard.</summary>
    public bool RecoveryPending => Volatile.Read(ref _recoveryPending);

    /// <summary>Runs a command.</summary>
    /// <remarks>
    /// Commands that replace or save the project are caught here rather than in the dispatcher,
    /// because they are about the session rather than about the project: which file is open, and
    /// whether what is open matches what is on disk.
    /// </remarks>
    public Task<CommandResult> ExecuteAsync(ICommand command, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command, DefaultIssuer, cancellationToken);

    /// <summary>Runs a command for someone.</summary>
    /// <param name="command">What to run.</param>
    /// <param name="issuer">Who asked: <c>gui</c>, <c>cli</c>, <c>mcp</c>, <c>rpc:&lt;client&gt;</c>.</param>
    /// <param name="cancellationToken">Gives up while it is still queued.</param>
    /// <remarks>
    /// Refused with <c>locked</c> while someone else holds the session. Opening, creating and
    /// saving are queued with everything else, so they never land in the middle of another
    /// client's command.
    /// </remarks>
    public async Task<CommandResult> ExecuteAsync(ICommand command, string issuer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);
        issuer ??= string.Empty;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        CommandResult result;
        IdScope? ids = null;
        if (HeldByAnother(issuer) is { } held)
        {
            result = CommandResult.Failure(Version, new CommandException(
                "locked",
                $"{held.Owner} holds the session ({held.Reason}) until {held.Until:HH:mm:ss}. Try again when it lets go."));
        }
        else
        {
            try
            {
                bool edit = CommandRegistry.Describe(command).Undoable || command is UndoCommand or RedoCommand;
                if (edit && RecoveryPending)
                {
                    // Editing without recovering: what the crash left is set aside, not lost.
                    await _dispatcher.RunExclusiveAsync(SetRecoveryAside, cancellationToken).ConfigureAwait(false);
                }

                ids = IdScope.Recording();
                result = command switch
                {
                    NewProjectCommand or OpenProjectCommand or SampleProjectCommand => await _dispatcher.RunExclusiveAsync(() => Replace(command, issuer), cancellationToken).ConfigureAwait(false),
                    SaveProjectCommand save => await _dispatcher.RunExclusiveAsync(() => Save(save), cancellationToken).ConfigureAwait(false),
                    AcceptRecoveryCommand accept => await _dispatcher.RunExclusiveAsync(() => Refusable(() => AcceptRecovery(accept, issuer)), cancellationToken).ConfigureAwait(false),
                    DiscardRecoveryCommand discard => await _dispatcher.RunExclusiveAsync(() => Refusable(() => DiscardRecovery(discard)), cancellationToken).ConfigureAwait(false),
                    _ => await _dispatcher.ExecuteAsync(command, issuer, ids, cancellationToken).ConfigureAwait(false),
                };
            }
            catch (CommandException refused)
            {
                // Opening over unsaved changes throws, as it always has; the console still hears.
                Remember(command, issuer, CommandResult.Failure(Version, refused));
                RaiseCompleted(command, issuer, CommandResult.Failure(Version, refused), System.Diagnostics.Stopwatch.GetElapsedTime(started));
                throw;
            }

            // What changes the project goes in the log, with the identifiers it made, and undo and
            // redo with it: the log is replayed over the saved project after a crash, so it has to
            // hold the edit exactly as it went. A press of play or an export is not an edit.
            if (result.Ok && _history is not null && command is not (NewProjectCommand or OpenProjectCommand or SampleProjectCommand or SaveProjectCommand)
                && (CommandRegistry.Describe(command).Undoable || command is UndoCommand or RedoCommand))
            {
                _history.Append(CommandRegistry.NameOf(command), CommandRegistry.ArgsToJson(command), _clock, ids?.Issued);
            }
        }

        Remember(command, issuer, result);
        RaiseCompleted(command, issuer, result, System.Diagnostics.Stopwatch.GetElapsedTime(started));
        return result;
    }

    /// <summary>
    /// Holds the session for one issuer, for a batch of their own: every other issuer's commands
    /// are refused with <c>locked</c> until it lets go or the time runs out. Queries still work.
    /// </summary>
    /// <param name="owner">Who holds it, as they issue commands.</param>
    /// <param name="reason">Why, for the people who are refused.</param>
    /// <param name="duration">How long at most; it lets go by itself after.</param>
    /// <param name="held">The lock held, the new one or the other issuer's.</param>
    /// <returns>True when the owner now holds it; false when someone else does.</returns>
    public bool TryLock(string owner, string reason, TimeSpan duration, out SessionLock held)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_lockGate)
        {
            if (Held() is { } current && !string.Equals(current.Owner, owner, StringComparison.Ordinal))
            {
                held = current;
                return false;
            }

            held = new SessionLock(owner, reason ?? string.Empty, _clock.GetUtcNow() + duration);
            _lock = held;
            return true;
        }
    }

    /// <summary>Lets go of the session. False when the owner did not hold it.</summary>
    public bool Unlock(string owner)
    {
        lock (_lockGate)
        {
            if (Held() is not { } current || !string.Equals(current.Owner, owner, StringComparison.Ordinal))
            {
                return false;
            }

            _lock = null;
            return true;
        }
    }

    /// <summary>The lock, unless it has run out; called under the gate.</summary>
    private SessionLock? Held()
    {
        if (_lock is { } current && current.Until <= _clock.GetUtcNow())
        {
            _lock = null;
        }

        return _lock;
    }

    private SessionLock? HeldByAnother(string issuer)
    {
        lock (_lockGate)
        {
            return Held() is { } current && !string.Equals(current.Owner, issuer, StringComparison.Ordinal) ? current : null;
        }
    }

    private void RaiseCompleted(ICommand command, string issuer, CommandResult result, TimeSpan elapsed)
    {
        if (CommandCompleted is null)
        {
            return;
        }

        var args = new CommandCompletedEventArgs(CommandRegistry.NameOf(command), CommandRegistry.ArgsToJson(command), issuer, result, _clock.GetUtcNow(), elapsed);
        foreach (Delegate subscriber in CommandCompleted.GetInvocationList())
        {
            try
            {
                ((EventHandler<CommandCompletedEventArgs>)subscriber)(this, args);
            }
            catch (Exception error)
            {
                _log.Error(error, "A CommandCompleted subscriber threw");
            }
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
    private CommandResult Replace(ICommand command, string issuer)
    {
        bool discard = command switch
        {
            NewProjectCommand create => create.Discard,
            OpenProjectCommand open => open.Discard,
            SampleProjectCommand sample => sample.Discard,
            _ => false,
        };

        if (IsDirty && !discard)
        {
            throw new CommandException(
                "unsaved-changes",
                "This project has unsaved changes. Save it, or pass --discard to throw them away.");
        }

        var context = new HandlerContext(_services, TimeProvider.System, ProjectPath);
        Project replacement = command switch
        {
            NewProjectCommand create => new Handlers.NewProjectHandler().Handle(Project, create, context),
            OpenProjectCommand open => new Handlers.OpenProjectHandler().Handle(Project, open, context),
            SampleProjectCommand sample => new Handlers.SampleProjectHandler().Handle(Project, sample, context),
            _ => throw new CommandException("not-a-replacement", "That command does not replace the project."),
        };

        string path = command is OpenProjectCommand opened ? System.IO.Path.GetFullPath(opened.Path) : string.Empty;

        // The project going was saved or deliberately discarded before this was allowed, so its
        // recovery files go; the one coming keeps whatever a crash left beside it, for the offer.
        _autosave?.Clear();
        _history?.Clear();

        ProjectPath = path;
        _dispatcher.Load(replacement, path, issuer);
        Interlocked.Exchange(ref _savedVersion, _dispatcher.Version);
        AttachRecovery(path);

        _log.Information("Session now holds {Project}", path.Length > 0 ? path : replacement.Name);
        return CommandResult.Success(_dispatcher.Version, context.ChangedIds);
    }

    /// <summary>
    /// Saves what can be saved when the process is about to die: the recovery copy beside a
    /// project that has a file, or the whole project in the rescue folder when it was never saved.
    /// Returns where it went, or null when there was nothing unsaved. Any thread; never throws.
    /// </summary>
    public string? Rescue()
    {
        try
        {
            if (!IsDirty)
            {
                return null;
            }

            if (_autosave is not null)
            {
                _autosave.OnCommand();
                return _autosave.WriteNow() ? _autosave.RecoveryPath : null;
            }

            return RecoveryService.RescueUntitled(Project);
        }
        catch (Exception error)
        {
            _log.Error(error, "Could not rescue the project");
            return null;
        }
    }

    /// <summary>
    /// Replaces the project with the one a crash left: the saved project with the history replayed,
    /// or the autosave copy with the commands after it, or a rescued untitled project. The session
    /// is then dirty, and a fresh recovery copy is written at once so a second crash loses nothing.
    /// </summary>
    private CommandResult AcceptRecovery(AcceptRecoveryCommand command, string issuer)
    {
        var service = new RecoveryService();
        var context = new HandlerContext(null, TimeProvider.System, ProjectPath);

        if (command.File is { Length: > 0 } file)
        {
            if (!File.Exists(file))
            {
                throw new CommandException("not-found", $"There is no rescued project at {file}.");
            }

            Project rescued = ProjectFile.Parse(File.ReadAllText(file), file).Project;
            _dispatcher.Load(rescued, ProjectPath, issuer);
            service.DiscardUntitled(file);
            _log.Information("Recovered the untitled project {Name} from {File}", rescued.Name, file);
            return CommandResult.Success(_dispatcher.Version, context.ChangedIds);
        }

        if (ProjectPath.Length == 0 || service.Find(ProjectPath) is not { } offer)
        {
            throw new CommandException("nothing-to-recover", "There is nothing to recover beside this project.");
        }

        RecoveryResult recovered = service.Replay(offer, _services);
        foreach (string skipped in recovered.Skipped)
        {
            Notices.Report(string.Empty, System.IO.Path.GetFileName(ProjectPath), "recovery-skipped", $"Not replayed while recovering: {skipped}", Core.Diagnostics.DiagnosticLevel.Warning);
        }

        _dispatcher.Load(recovered.Project, ProjectPath, issuer);
        SetRecoveryAside();

        // Dirty from here, since the file is still what was last saved, and written down at once.
        _autosave?.OnCommand();
        _autosave?.WriteNow();

        _log.Information(
            "Recovered {Project} from the {From}: {Replayed} commands replayed, {Skipped} skipped",
            ProjectPath,
            recovered.From,
            recovered.Replayed,
            recovered.Skipped.Count);
        return CommandResult.Success(_dispatcher.Version, context.ChangedIds);
    }

    /// <summary>A session command whose refusal is a failed result, as a project command's is.</summary>
    private CommandResult Refusable(Func<CommandResult> action)
    {
        try
        {
            return action();
        }
        catch (CommandException refused)
        {
            return CommandResult.Failure(Version, refused);
        }
    }

    /// <summary>Declines the recovery: the files are set aside in the sidecar and the project stays as saved.</summary>
    private CommandResult DiscardRecovery(DiscardRecoveryCommand command)
    {
        if (command.File is { Length: > 0 } file)
        {
            new RecoveryService().DiscardUntitled(file);
        }
        else
        {
            SetRecoveryAside();
        }

        return CommandResult.Success(_dispatcher.Version, []);
    }

    /// <summary>
    /// Points autosave and the history at a project's sidecar, or at nothing for one that has
    /// never been saved. What a crash left there stays untouched until someone decides: accept,
    /// discard, or start editing, which sets it aside (Phase 33); until then nothing is appended
    /// to a history that belongs to the last session.
    /// </summary>
    private void AttachRecovery(string path)
    {
        _autosave?.Dispose();
        _autosave = null;
        _history?.Dispose();
        _history = null;
        Volatile.Write(ref _recoveryPending, false);

        if (!_recoveryEnabled || path.Length == 0)
        {
            return;
        }

        Volatile.Write(ref _recoveryPending, RecoveryService.HasLeftovers(path));
        _autosave = new AutosaveService(path, _clock);
        _autosave.Watch(() => _dispatcher.Project);
        if (!RecoveryPending)
        {
            _history = new HistoryLog(path);
        }
    }

    /// <summary>Archives what a crash left beside the project and starts a fresh history.</summary>
    private CommandResult SetRecoveryAside()
    {
        if (ProjectPath.Length > 0)
        {
            _history?.Dispose();
            _history = null;
            new RecoveryService().Archive(ProjectPath);

            if (_recoveryEnabled)
            {
                _history = new HistoryLog(ProjectPath);
            }
        }

        Volatile.Write(ref _recoveryPending, false);
        return CommandResult.Success(_dispatcher.Version, []);
    }

    /// <summary>Keeps the last fifty commands for a crash report.</summary>
    private void Remember(ICommand command, string issuer, CommandResult result)
    {
        string arguments;
        try
        {
            arguments = CommandRegistry.ArgsToJson(command).ToJsonString();
        }
        catch (ArgumentException)
        {
            // A refused command with a number JSON cannot hold (NaN, infinity).
            arguments = command.ToString() ?? string.Empty;
        }

        var entry = new RecentCommand(_clock.GetUtcNow(), CommandRegistry.NameOf(command), arguments, issuer, result.Ok, result.Code);

        lock (_recentGate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > RecentLimit)
            {
                _recent.Dequeue();
            }
        }
    }

    /// <summary>Writes the project and marks the session clean.</summary>
    private CommandResult Save(SaveProjectCommand command)
    {
        var context = new HandlerContext(null, TimeProvider.System, ProjectPath);
        Project written = new Handlers.SaveProjectHandler().Handle(Project, command, context);

        string before = ProjectPath;
        ProjectPath = System.IO.Path.GetFullPath(command.Path ?? ProjectPath);
        _dispatcher.ProjectPath = ProjectPath;
        if (!string.Equals(before, ProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            // Saved as another file, or for the first time: recovery follows it there.
            _autosave?.Clear();
            _history?.Clear();
            AttachRecovery(ProjectPath);
        }

        // Saving can change the project, because an absolute media path becomes a relative one.
        // That is not an edit, so it does not go on the undo stack, but the session has to hold
        // what was actually written or the next save would write something different again.
        if (written != Project)
        {
            _dispatcher.Load(written, ProjectPath, DefaultIssuer);
            _dispatcher.Undo.Clear();
        }

        Interlocked.Exchange(ref _savedVersion, _dispatcher.Version);

        if (RecoveryPending)
        {
            SetRecoveryAside();
        }

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
