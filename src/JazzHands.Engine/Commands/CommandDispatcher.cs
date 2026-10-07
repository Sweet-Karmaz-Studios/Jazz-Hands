using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
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
/// <param name="Issuer">Who asked for it: <c>gui</c>, <c>cli</c>, <c>mcp</c>, <c>rpc:&lt;client&gt;</c>, or empty when nobody said.</param>
/// <param name="Command">The command that ran, or null for a load.</param>
public sealed record ProjectChangedEventArgs(
    long Version,
    Project Project,
    string CommandName,
    ImmutableArray<string> ChangedIds,
    ChangeOrigin Origin,
    string Issuer = "",
    ICommand? Command = null);

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

    private static readonly ConcurrentDictionary<Type, IHandlerAdapter> Adapters = new();
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

    /// <summary>What handlers use to queue a command for later; the session sets it to its own queue.</summary>
    public Func<ICommand, string, Task<CommandResult>>? Later { get; set; }

    /// <summary>Runs a command and waits for it.</summary>
    /// <exception cref="CommandException">The command could not be done.</exception>
    public Task<CommandResult> ExecuteAsync(ICommand command, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command, string.Empty, cancellationToken);

    /// <summary>Runs a command for someone and waits for it.</summary>
    /// <param name="command">What to run.</param>
    /// <param name="issuer">Who asked, as the history and the change event will say it.</param>
    /// <param name="cancellationToken">Gives up while it is still queued.</param>
    public Task<CommandResult> ExecuteAsync(ICommand command, string issuer, CancellationToken cancellationToken = default) =>
        ExecuteAsync(command, issuer, null, cancellationToken);

    /// <summary>Runs a command for someone with the identifiers it makes recorded or replayed.</summary>
    /// <param name="command">What to run.</param>
    /// <param name="issuer">Who asked.</param>
    /// <param name="ids">Active on the dispatcher's thread while the command runs; null for none.</param>
    /// <param name="cancellationToken">Gives up while it is still queued.</param>
    public async Task<CommandResult> ExecuteAsync(ICommand command, string issuer, IdScope? ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A handler's slow part runs first, off the queue (IPreparingHandler): the commands asked
        // for meanwhile go on. Its refusal is the command's. A replay of the history log does
        // not prepare: the handler does the work in its turn, where the recorded identifiers are.
        object? prepared = null;
        if (ids is not { IsReplaying: true } && Preparer(command) is { } prepare)
        {
            try
            {
                var context = new HandlerContext(_services, _clock, ProjectPath) { Later = Later, Cancellation = cancellationToken };
                Project now = Project;
                prepared = await Task.Run(
                    () =>
                    {
                        // The identifiers made here are the command's: kept with what was
                        // prepared, and written down when the handler takes it up.
                        using IdScope made = IdScope.Recording().Enter();
                        object? result = prepare(now, context);
                        return result is Handlers.PreparedWork work ? work with { Ids = [.. made.Issued] } : result;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CommandException error)
            {
                _log.Debug("{Command} refused while preparing: {Code}", CommandRegistry.NameOf(command), error.Code);
                return CommandResult.Failure(Version, error);
            }
        }

        return await Enqueue(new Job(command, NewCompletion(), issuer ?? string.Empty, Ids: ids, Token: cancellationToken, Prepared: prepared), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The prepare step of a command's handler, or null when it has none.</summary>
    private Func<Project, HandlerContext, object?>? Preparer(ICommand command)
    {
        if (command is UndoCommand or RedoCommand or BatchCommand)
        {
            return null;
        }

        Type type = command.GetType();
        Type preparing = typeof(IPreparingHandler<>).MakeGenericType(type);
        object? handler = _services.GetService(typeof(ICommandHandler<>).MakeGenericType(type));
        if (handler is null || !preparing.IsInstanceOfType(handler))
        {
            return null;
        }

        System.Reflection.MethodInfo method = preparing.GetMethod(nameof(IPreparingHandler<ICommand>.Prepare))!;
        return (project, context) => method.Invoke(handler, System.Reflection.BindingFlags.DoNotWrapExceptions, null, [project, command, context], null);
    }

    /// <summary>
    /// Runs something that is about the session rather than a command on the project, such as
    /// opening another project or saving this one, in its turn in the queue, so it never lands in
    /// the middle of a command from another client.
    /// </summary>
    /// <param name="action">What to run, on the queue's thread.</param>
    /// <param name="cancellationToken">Gives up while it is still queued.</param>
    public Task<CommandResult> RunExclusiveAsync(Func<CommandResult> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Enqueue(new Job(null, NewCompletion(), string.Empty, action), cancellationToken);
    }

    private static TaskCompletionSource<CommandResult> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<CommandResult> Enqueue(Job job, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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
    public void Load(Project project, string path = "", string issuer = "")
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
            ChangeOrigin.Load,
            issuer));
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

            if (job.Exclusive is { } exclusive)
            {
                // Session work: its own exceptions are its caller's to see, as they were before
                // it was queued.
                try
                {
                    job.Completion.TrySetResult(exclusive());
                }
                catch (Exception error)
                {
                    job.Completion.TrySetException(error);
                }

                continue;
            }

            try
            {
                using (job.Ids?.Enter())
                {
                    job.Completion.TrySetResult(Run(job.Command!, job.Issuer, job.Token, job.Prepared));
                }
            }
            catch (CommandException error)
            {
                _log.Debug("{Command} refused: {Code}", CommandRegistry.NameOf(job.Command!), error.Code);
                job.Completion.TrySetResult(CommandResult.Failure(Version, error));
            }
            catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
            {
                // Stopped part way by whoever asked; the project is untouched.
                _log.Information("{Command} was cancelled", CommandRegistry.NameOf(job.Command!));
                job.Completion.TrySetCanceled(job.Token);
            }
            catch (Exception error)
            {
                // A real bug. The project is untouched, because it is only replaced once a
                // handler has returned, but the caller has to hear about it.
                _log.Error(error, "{Command} threw", CommandRegistry.NameOf(job.Command!));
                job.Completion.TrySetException(error);
            }
        }
    }

    private CommandResult Run(ICommand command, string issuer, CancellationToken cancellation, object? prepared = null) => command switch
    {
        UndoCommand undo => RunUndo(undo, issuer),
        RedoCommand redo => RunRedo(redo, issuer),
        BatchCommand batch => RunBatch(batch, issuer, cancellation),
        _ => RunOne(command, issuer, cancellation, prepared),
    };

    private CommandResult RunOne(ICommand command, string issuer, CancellationToken cancellation, object? prepared = null)
    {
        CommandMetadata metadata = CommandRegistry.Describe(command);
        Project before = _project;

        var context = new HandlerContext(_services, _clock, ProjectPath) { Later = Later, Cancellation = cancellation, Prepared = prepared };
        Project after = LiftAdded(before, FollowLifted(before, SettleTitles(before, SettleTransitions(before, Magnetize(before, Apply(before, command, context), context), context), context), context), context);

        return Commit(command, metadata, before, after, context.ChangedIds, ChangeOrigin.Command, issuer);
    }

    /// <summary>
    /// Runs a batch, and leaves nothing behind if any part of it fails.
    /// </summary>
    /// <remarks>
    /// The commands run against each other's output, so a batch that adds a clip and then trims
    /// it works. Nothing is committed until all of them have returned, so a failure half way
    /// through takes the whole batch with it.
    /// </remarks>
    private CommandResult RunBatch(BatchCommand batch, string issuer, CancellationToken cancellation)
    {
        Project before = _project;
        Project working = before;
        var context = new HandlerContext(_services, _clock, ProjectPath) { Later = Later, Cancellation = cancellation };

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

        working = LiftAdded(before, FollowLifted(before, SettleTitles(before, SettleTransitions(before, Magnetize(before, working, context), context), context), context), context);
        CommandMetadata metadata = CommandRegistry.Describe(batch);
        return Commit(batch, metadata, before, working, context.ChangedIds, ChangeOrigin.Command, issuer);
    }

    private Project Apply(Project project, ICommand command, HandlerContext context) => Apply(_services, project, command, context);

    /// <summary>
    /// Runs a command's handler against a project and returns what it made, committing nothing:
    /// the dispatcher's own step, and a handler's that builds a project from other commands.
    /// </summary>
    internal static Project Apply(IServiceProvider services, Project project, ICommand command, HandlerContext context)
    {
        Type handlerType = typeof(ICommandHandler<>).MakeGenericType(command.GetType());
        object handler = services.GetService(handlerType)
            ?? throw new CommandException(
                "no-handler",
                $"Nothing handles '{CommandRegistry.NameOf(command)}'. Add an {handlerType.Name} and register it.");

        IHandlerAdapter adapter = Adapters.GetOrAdd(
            command.GetType(),
            static type => (IHandlerAdapter)Activator.CreateInstance(
                typeof(HandlerAdapter<>).MakeGenericType(type))!);

        return adapter.Handle(handler, project, command, context);
    }

    /// <summary>
    /// Closes the gaps an edit left on the primary track of every magnetic sequence it touched.
    /// </summary>
    /// <remarks>
    /// Here rather than in each handler, so no edit can forget it, and once per command or batch,
    /// so a batch that opens a gap and fills it again does not ripple twice. A sequence the command
    /// did not touch is left alone: it was settled when it was last edited. When the gaps cannot
    /// close, because a sync-locked clip is in the way, the command is refused rather than leaving
    /// a magnetic storyline with a hole in it.
    /// </remarks>
    private static Project Magnetize(Project before, Project after, HandlerContext context)
    {
        if (ReferenceEquals(before, after))
        {
            return after;
        }

        Project result = after;
        foreach (Sequence sequence in after.Sequences)
        {
            if (!sequence.IsMagnetic || ReferenceEquals(before.Sequence(sequence.Id), sequence))
            {
                continue;
            }

            Sequence closed = HandlerContext.Require(EditOps.Magnetize(sequence));
            if (!ReferenceEquals(closed, sequence))
            {
                context.Changed(EditOps.Changed(sequence, closed));
                result = result.ReplaceSequence(closed);
            }
        }

        return result;
    }

    /// <summary>
    /// Moves the out animation of every title an edit made longer or shorter to its new end.
    /// </summary>
    /// <remarks>
    /// A title's animations are keyframes from its start (decision 198), so a trim at its end
    /// would otherwise leave it fading out early or not at all. Like the transitions, this is part
    /// of the edit that caused it, whatever the edit was.
    /// </remarks>
    private static Project SettleTitles(Project before, Project after, HandlerContext context)
    {
        if (ReferenceEquals(before, after))
        {
            return after;
        }

        Project result = after;
        foreach (Sequence sequence in after.Sequences)
        {
            if (ReferenceEquals(before.Sequence(sequence.Id), sequence))
            {
                continue;
            }

            foreach (Track track in sequence.Tracks)
            {
                Track settled = track;
                foreach (Clip clip in track.Clips)
                {
                    if (!string.Equals(clip.GeneratorId, Core.Titles.TitleParams.GeneratorId, StringComparison.Ordinal)
                        || before.FindClip(clip.Id)?.Clip is not { } was
                        || was.Duration == clip.Duration
                        || clip.Effects.FirstOrDefault(effect => Core.Effects.EffectChains.IsOwnParameters(clip, effect)) is not { } own)
                    {
                        continue;
                    }

                    Effect moved = Core.Titles.TitleAnimations.Retime(own, was.Duration, clip.Duration);
                    if (!ReferenceEquals(moved, own))
                    {
                        settled = settled.ReplaceClip(clip with { Effects = clip.Effects.SetItem(clip.Effects.IndexOf(effect => ReferenceEquals(effect, own)), moved) });
                        context.Changed(clip.Id);
                    }
                }

                if (!ReferenceEquals(settled, track))
                {
                    result = result.ReplaceTrack(settled);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Lifts a reframed sequence's graphics again when the original's track they came from changed,
    /// so a title edited in the landscape sequence is edited in the vertical one too.
    /// </summary>
    /// <remarks>
    /// Part of the edit that caused it, like the other settling passes, so one undo takes back both.
    /// A lifted track whose own clips an edit changed has been made by hand: it stops following
    /// (<see cref="Track.Lifted"/> cleared) rather than have the next edit to the original undo the
    /// work. Its other settings (height, lock, mute) do not count. Clips keep their ids where the
    /// original's do, so a selection in the vertical sequence survives an edit to the landscape.
    /// </remarks>
    private static Project FollowLifted(Project before, Project after, HandlerContext context)
    {
        if (ReferenceEquals(before, after))
        {
            return after;
        }

        Project result = after;
        foreach (Sequence sequence in after.Sequences)
        {
            foreach (Track track in sequence.Tracks)
            {
                if (track.Lifted is not { } lifted)
                {
                    continue;
                }

                Track? was = before.Sequence(sequence.Id)?.Track(track.Id);
                Track? sourceWas = before.Sequences.Select(other => other.Track(lifted.TrackId)).FirstOrDefault(found => found is not null);
                Track? source = after.Sequences.Select(other => other.Track(lifted.TrackId)).FirstOrDefault(found => found is not null);

                if (was is not null && was.Clips != track.Clips)
                {
                    // Edited by hand in the vertical sequence: it keeps that, and follows no more.
                    result = result.ReplaceTrack(track with { Lifted = null });
                    context.Changed(track.Id);
                    continue;
                }

                if (source is null || ReferenceEquals(source, sourceWas) || !Handlers.ReframeSequenceHandler.IsGraphics(source))
                {
                    continue;
                }

                // The ids the lifted clips had, by the original's clip they came from.
                var ids = new Dictionary<string, string>(StringComparer.Ordinal);
                if (sourceWas is not null && sourceWas.Clips.Length == track.Clips.Length)
                {
                    for (int index = 0; index < track.Clips.Length; index++)
                    {
                        ids[sourceWas.Clips[index].Id] = track.Clips[index].Id;
                    }
                }

                Track lift = Handlers.ReframeSequenceHandler.Lift(source, (float)lifted.Across, (float)lifted.Down, id => ids.GetValueOrDefault(id));
                Track followed = track with { Clips = lift.Clips, Transitions = lift.Transitions };
                if (followed == track)
                {
                    continue;
                }

                result = result.ReplaceTrack(followed);
                context.Changed(track.Id);
                context.Changed(followed.Clips.Select(clip => clip.Id));
            }
        }

        return result;
    }

    /// <summary>
    /// Lifts a track that an edit made graphics in a reframed sequence's original into the vertical
    /// version, as reframing would have: left out of the nests showing the original, and copied
    /// above the picture, following the original from then on.
    /// </summary>
    /// <remarks>
    /// Runs after <see cref="FollowLifted"/>, so the copies it makes are not lifted twice. A track the
    /// nests already leave out was lifted before, even if its copy has since been edited by hand and
    /// follows no more, so it is not lifted again. Without a nest of the original the vertical
    /// version is no longer one, and nothing is lifted.
    /// </remarks>
    private static Project LiftAdded(Project before, Project after, HandlerContext context)
    {
        if (ReferenceEquals(before, after))
        {
            return after;
        }

        Project result = after;
        foreach (Sequence made in after.Sequences)
        {
            if (made.Reframed is not { } from
                || after.Sequence(from.SequenceId) is not { } original
                || ReferenceEquals(before.Sequence(original.Id), original))
            {
                continue;
            }

            Sequence vertical = result.Sequence(made.Id)!;
            Sequence? originalWas = before.Sequence(original.Id);

            // The clips showing the original: the blurred copy here, and the window's pan in the crop it nests.
            ImmutableArray<Clip> nests =
            [
                .. vertical.Tracks.SelectMany(track => track.Clips).Where(clip => clip.SequenceId == original.Id),
                .. vertical.Tracks.SelectMany(track => track.Clips)
                    .Where(clip => clip.SequenceId is { } nested && nested != original.Id)
                    .Select(clip => result.Sequence(clip.SequenceId!))
                    .OfType<Sequence>()
                    .SelectMany(crop => crop.Tracks.SelectMany(track => track.Clips))
                    .Where(clip => clip.SequenceId == original.Id),
            ];
            if (nests.IsEmpty)
            {
                continue;
            }

            Track[] added =
            [
                .. original.Tracks.Where(track =>
                    !ReferenceEquals(originalWas?.Track(track.Id), track)
                    && Handlers.ReframeSequenceHandler.IsGraphics(track)
                    && !nests.Any(clip => clip.HiddenTracks.Contains(track.Id))
                    && !vertical.Tracks.Any(lifted => lifted.Lifted?.TrackId == track.Id)),
            ];

            foreach (Track track in added)
            {
                // Above the picture and the graphics lifted before it, below the sound.
                Track lift = Handlers.ReframeSequenceHandler.Lift(track, (float)from.Across, (float)from.Down) with
                {
                    Id = Id.New(),
                    Order = vertical.NextTrackOrder(),
                    Lifted = new LiftedTrack(track.Id, from.Across, from.Down),
                };

                int above = vertical.Tracks.OrderBy(other => other.Order).ToList().FindLastIndex(other => other.Kind != TrackKind.Audio);
                vertical = Handlers.TrackOrder.Renumber(vertical.AddTrack(lift), lift.Id, above + 1, context);
                context.Changed(lift.Id);
                context.Changed(lift.Clips.Select(clip => clip.Id));
            }

            if (added.Length == 0)
            {
                continue;
            }

            context.Changed(vertical.Id);
            result = result.ReplaceSequence(vertical);
            foreach (Clip nest in nests)
            {
                Track holder = result.TrackOf(nest.Id)!;
                Clip hidden = nest with { HiddenTracks = [.. nest.HiddenTracks, .. added.Select(track => track.Id)] };
                result = result.ReplaceTrack(holder.ReplaceClip(hidden));
                context.Changed(nest.Id);
            }
        }

        return result;
    }

    /// <summary>
    /// Removes the transitions whose clips an edit separated, on every sequence it touched.
    /// </summary>
    /// <remarks>
    /// Here rather than in each handler, like the magnetic timeline, so no edit can leave a
    /// transition naming clips that no longer meet: a move, a delete, a trim that opens a gap. It
    /// is part of the edit that caused it, so one undo brings the clips and the transition back.
    /// </remarks>
    private static Project SettleTransitions(Project before, Project after, HandlerContext context)
    {
        if (ReferenceEquals(before, after))
        {
            return after;
        }

        Project result = after;
        foreach (Sequence sequence in after.Sequences)
        {
            if (ReferenceEquals(before.Sequence(sequence.Id), sequence))
            {
                continue;
            }

            (Sequence settled, ImmutableArray<string> removed) = TransitionOps.Settle(sequence);
            if (removed.IsEmpty)
            {
                continue;
            }

            context.Changed(removed);
            foreach (Track track in sequence.Tracks)
            {
                if (!ReferenceEquals(settled.Track(track.Id), track))
                {
                    context.Changed(track.Id);
                }
            }

            result = result.ReplaceSequence(settled);
        }

        return result;
    }

    private CommandResult Commit(
        ICommand command,
        CommandMetadata metadata,
        Project before,
        Project after,
        ImmutableArray<string> changed,
        ChangeOrigin origin,
        string issuer)
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
            Undo.Push(new UndoEntry(command, before, after, changed, _clock.GetUtcNow(), issuer));
        }

        Raise(new ProjectChangedEventArgs(version, after, metadata.Name, changed, origin, issuer, command));
        return CommandResult.Success(version, changed);
    }

    private CommandResult RunUndo(UndoCommand command, string issuer)
    {
        int steps = command.Steps;
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);

        var changed = ImmutableArray.CreateBuilder<string>();
        Project project = _project;

        for (int step = 0; step < steps; step++)
        {
            UndoEntry entry = Undo.Undo();
            project = entry.Before;
            changed.AddRange(entry.ChangedIds);
        }

        return Settle(project, changed.ToImmutable(), ChangeOrigin.Undo, issuer, command);
    }

    private CommandResult RunRedo(RedoCommand command, string issuer)
    {
        int steps = command.Steps;
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);

        var changed = ImmutableArray.CreateBuilder<string>();
        Project project = _project;

        for (int step = 0; step < steps; step++)
        {
            UndoEntry entry = Undo.Redo();
            project = entry.After;
            changed.AddRange(entry.ChangedIds);
        }

        return Settle(project, changed.ToImmutable(), ChangeOrigin.Redo, issuer, command);
    }

    private CommandResult Settle(Project project, ImmutableArray<string> changed, ChangeOrigin origin, string issuer, ICommand command)
    {
        Volatile.Write(ref _project, project);
        long version = Interlocked.Increment(ref _version);

        Raise(new ProjectChangedEventArgs(version, project, origin.ToString().ToLowerInvariant(), changed, origin, issuer, command));
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

    /// <summary>A command to run for someone, or session work to run in its turn.</summary>
    private sealed record Job(ICommand? Command, TaskCompletionSource<CommandResult> Completion, string Issuer, Func<CommandResult>? Exclusive = null, IdScope? Ids = null, CancellationToken Token = default, object? Prepared = null);

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
