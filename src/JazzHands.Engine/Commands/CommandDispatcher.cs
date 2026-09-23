using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using Serilog;

namespace JazzHands.Engine.Commands;

/// <summary>Where a change came from, so a client can ignore its own echo.</summary>
public enum ChangeOrigin
{
    /// <summary>A command ran.</summary>
    Command,

    /// <summary>A command was taken back.</summary>
    Undo,

    /// <summary>A command was put back.</summary>
    Redo,

    /// <summary>The whole project was replaced, by opening or creating one.</summary>
    Load,
}

/// <summary>Raised after the project changes.</summary>
/// <param name="Version">The project version after the change.</param>
/// <param name="Project">The project as it now is.</param>
/// <param name="CommandName">What ran, or empty when the project was replaced.</param>
/// <param name="ChangedIds">The ids of everything that was touched.</param>
/// <param name="Origin">Whether this was a command, an undo, a redo, or a load.</param>
public sealed record ProjectChangedEventArgs(
    long Version,
    Project Project,
    string CommandName,
    ImmutableArray<string> ChangedIds,
    ChangeOrigin Origin);

/// <summary>
/// The one place a project is allowed to change.
/// </summary>
/// <remarks>
/// Commands arrive from the GUI thread, from JSON-RPC connections, from MCP, and from the CLI, so
/// they arrive concurrently. They are run one at a time through a queue with a single reader, and
/// not under a lock: a lock would let a slow handler block a UI thread that was only trying to
/// enqueue, and would give no guarantee about the order two callers came out in. A queue gives
/// both, and makes the history mean something, because the history is the order the queue ran
/// things in.
///
/// Handlers are pure with respect to the project, so nothing here has to defend against a handler
/// mutating what it was given. What it does defend against is a handler throwing: the project is
/// only replaced once a handler has returned, so a failed command leaves everything exactly as it
/// was.
/// </remarks>
public sealed class CommandDispatcher : IAsyncDisposable
{
    private readonly ILogger _log = Log.ForContext<CommandDispatcher>();
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });

    private readonly ConcurrentDictionary<Type, IHandlerAdapter> _adapters = new();
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly Task _pump;

    private Project _project;
    private long _version;
    private bool _disposed;

    /// <summary>Creates a dispatcher over a project.</summary>
    /// <param name="project">The project to start with.</param>
    /// <param name="services">Where handlers come from.</param>
    /// <param name="clock">The clock, injected for tests.</param>
    /// <param name="undoLimit">How many commands to remember.</param>
    public CommandDispatcher(
        Project project,
        IServiceProvider services,
        TimeProvider? clock = null,
        int undoLimit = 1000)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(services);

        _project = project;
        _services = services;
        _clock = clock ?? TimeProvider.System;
        Undo = new UndoStack(undoLimit);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Raised after every change, on the thread that ran the command.</summary>
    public event EventHandler<ProjectChangedEventArgs>? ProjectChanged;

    /// <summary>The project as it now is.</summary>
    /// <remarks>
    /// Safe to read from any thread: the reference is replaced atomically and the project itself
    /// is immutable, so a reader either sees the version before a command or the version after.
    /// </remarks>
    public Project Project => Volatile.Read(ref _project);

    /// <summary>How many commands have been applied since the dispatcher was created.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>The undo history.</summary>
    public UndoStack Undo { get; }

    /// <summary>Where the project lives, for handlers that need to resolve a path.</summary>
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>Runs a command and waits for it.</summary>
    /// <exception cref="CommandException">The command could not be done.</exception>
    public async Task<CommandResult> ExecuteAsync(ICommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var job = new Job(command, new TaskCompletionSource<CommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously));

        if (!_queue.Writer.TryWrite(job))
        {
            throw new CommandException("dispatcher-closed", "The session is closing.");
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<CommandResult>)state!).TrySetCanceled(),
            job.Completion);

        return await job.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a query against the project as it is now.
    /// </summary>
    /// <remarks>
    /// Not queued. A query cannot change anything, and the project it reads is an immutable
    /// snapshot, so there is nothing to serialize against and no reason to make the caller wait
    /// behind an export.
    /// </remarks>
    public TResult Query<TResult>(IQuery<TResult> query, ISessionState? session = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Type handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
        object handler = _services.GetService(handlerType)
            ?? throw new CommandException(
                "no-handler",
                $"Nothing answers '{CommandRegistry.NameOf(query)}'. Add an {handlerType.Name} and register it.");

        Type adapterType = typeof(QueryAdapter<,>).MakeGenericType(query.GetType(), typeof(TResult));
        var adapter = (IQueryAdapter<TResult>)Activator.CreateInstance(adapterType)!;

        return adapter.Handle(handler, Project, query, new QueryContext(session, _services));
    }

    /// <summary>Replaces the project wholesale, as opening or creating one does.</summary>
    /// <remarks>
    /// Clears the undo history, because undo cannot cross from one project into another. The
    /// version keeps counting, so a client that saw version 12 of the old project does not
    /// mistake version 3 of the new one for something it already has.
    /// </remarks>
    public void Load(Project project, string path = "")
    {
        ArgumentNullException.ThrowIfNull(project);

        Volatile.Write(ref _project, project);
        ProjectPath = path;
        Undo.Clear();

        Raise(new ProjectChangedEventArgs(
            Interlocked.Increment(ref _version),
            project,
            string.Empty,
            [],
            ChangeOrigin.Load));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        await foreach (Job job in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (job.Completion.Task.IsCompleted)
            {
                // The caller gave up while it was queued.
                continue;
            }

            try
            {
                job.Completion.TrySetResult(Run(job.Command));
            }
            catch (CommandException error)
            {
                _log.Debug("{Command} refused: {Code}", CommandRegistry.NameOf(job.Command), error.Code);
                job.Completion.TrySetResult(CommandResult.Failure(Version, error));
            }
            catch (Exception error)
            {
                // A real bug. The project is untouched, because it is only replaced once a
                // handler has returned, but the caller has to hear about it.
                _log.Error(error, "{Command} threw", CommandRegistry.NameOf(job.Command));
                job.Completion.TrySetException(error);
            }
        }
    }

    private CommandResult Run(ICommand command) => command switch
    {
        UndoCommand undo => RunUndo(undo.Steps),
        RedoCommand redo => RunRedo(redo.Steps),
        BatchCommand batch => RunBatch(batch),
        _ => RunOne(command),
    };

    private CommandResult RunOne(ICommand command)
    {
        CommandMetadata metadata = CommandRegistry.Describe(command);
        Project before = _project;

        var context = new HandlerContext(_services, _clock, ProjectPath);
        Project after = Apply(before, command, context);

        return Commit(command, metadata, before, after, context.ChangedIds, ChangeOrigin.Command);
    }

    /// <summary>
    /// Runs a batch, and leaves nothing behind if any part of it fails.
    /// </summary>
    /// <remarks>
    /// The commands run against each other's output, so a batch that adds a clip and then trims
    /// it works. Nothing is committed until all of them have returned, so a failure half way
    /// through takes the whole batch with it.
    /// </remarks>
    private CommandResult RunBatch(BatchCommand batch)
    {
        Project before = _project;
        Project working = before;
        var context = new HandlerContext(_services, _clock, ProjectPath);

        foreach (ICommand step in batch.Commands)
        {
            working = step switch
            {
                BatchCommand nested => throw new CommandException(
                    "nested-batch",
                    "A batch cannot hold a batch. Flatten it."),
                UndoCommand or RedoCommand => throw new CommandException(
                    "undo-in-batch",
                    "A batch cannot undo or redo. It is itself one undo step."),
                _ => Apply(working, step, context),
            };
        }

        CommandMetadata metadata = CommandRegistry.Describe(batch);
        return Commit(batch, metadata, before, working, context.ChangedIds, ChangeOrigin.Command);
    }

    private Project Apply(Project project, ICommand command, HandlerContext context)
    {
        Type handlerType = typeof(ICommandHandler<>).MakeGenericType(command.GetType());
        object handler = _services.GetService(handlerType)
            ?? throw new CommandException(
                "no-handler",
                $"Nothing handles '{CommandRegistry.NameOf(command)}'. Add an {handlerType.Name} and register it.");

        IHandlerAdapter adapter = _adapters.GetOrAdd(
            command.GetType(),
            static type => (IHandlerAdapter)Activator.CreateInstance(
                typeof(HandlerAdapter<>).MakeGenericType(type))!);

        return adapter.Handle(handler, project, command, context);
    }

    private CommandResult Commit(
        ICommand command,
        CommandMetadata metadata,
        Project before,
        Project after,
        ImmutableArray<string> changed,
        ChangeOrigin origin)
    {
        if (ReferenceEquals(before, after))
        {
            // A command that changed nothing is still a success, but it does not deserve an undo
            // step: pressing ctrl+Z should not consume one doing nothing.
            return CommandResult.Success(Version, changed);
        }

        after = after.Touch(_clock);
        Volatile.Write(ref _project, after);
        long version = Interlocked.Increment(ref _version);

        if (metadata.Undoable)
        {
            Undo.Push(new UndoEntry(command, before, after, changed, _clock.GetUtcNow()));
        }

        Raise(new ProjectChangedEventArgs(version, after, metadata.Name, changed, origin));
        return CommandResult.Success(version, changed);
    }

    private CommandResult RunUndo(int steps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);

        var changed = ImmutableArray.CreateBuilder<string>();
        Project project = _project;

        for (int step = 0; step < steps; step++)
        {
            UndoEntry entry = Undo.Undo();
            project = entry.Before;
            changed.AddRange(entry.ChangedIds);
        }

        return Settle(project, changed.ToImmutable(), ChangeOrigin.Undo);
    }

    private CommandResult RunRedo(int steps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);

        var changed = ImmutableArray.CreateBuilder<string>();
        Project project = _project;

        for (int step = 0; step < steps; step++)
        {
            UndoEntry entry = Undo.Redo();
            project = entry.After;
            changed.AddRange(entry.ChangedIds);
        }

        return Settle(project, changed.ToImmutable(), ChangeOrigin.Redo);
    }

    private CommandResult Settle(Project project, ImmutableArray<string> changed, ChangeOrigin origin)
    {
        Volatile.Write(ref _project, project);
        long version = Interlocked.Increment(ref _version);

        Raise(new ProjectChangedEventArgs(version, project, origin.ToString().ToLowerInvariant(), changed, origin));
        return CommandResult.Success(version, changed);
    }

    private void Raise(ProjectChangedEventArgs args)
    {
        // A subscriber that throws is a bug in the subscriber, and must not take down the
        // dispatcher or roll back a command that has already happened.
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

    private sealed record Job(ICommand Command, TaskCompletionSource<CommandResult> Completion);

    private interface IHandlerAdapter
    {
        Project Handle(object handler, Project project, ICommand command, HandlerContext context);
    }

    /// <summary>Calls a typed handler from the untyped dispatcher, without reflection per command.</summary>
    private sealed class HandlerAdapter<TCommand> : IHandlerAdapter
        where TCommand : ICommand
    {
        public Project Handle(object handler, Project project, ICommand command, HandlerContext context) =>
            ((ICommandHandler<TCommand>)handler).Handle(project, (TCommand)command, context);
    }

    private interface IQueryAdapter<out TResult>
    {
        TResult Handle(object handler, Project project, object query, QueryContext context);
    }

    private sealed class QueryAdapter<TQuery, TResult> : IQueryAdapter<TResult>
        where TQuery : IQuery<TResult>
    {
        public TResult Handle(object handler, Project project, object query, QueryContext context) =>
            ((IQueryHandler<TQuery, TResult>)handler).Handle(project, (TQuery)query, context);
    }
}
