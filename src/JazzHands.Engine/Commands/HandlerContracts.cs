using System.Collections.Immutable;
using JazzHands.Core.Commands;
using JazzHands.Core.Editing;
using JazzHands.Core.Model;

namespace JazzHands.Engine.Commands;

/// <summary>
/// What a handler is given besides the project and the command.
/// </summary>
/// <remarks>
/// Handlers are pure with respect to the project: they take one and return another. Everything
/// else they might need arrives here, which is what keeps them testable without a session, a
/// disk or a GPU.
///
/// The changed-id list is the other half of the contract. Viewmodels and remote clients redraw
/// what changed rather than everything, so a handler that forgets to report a clip leaves a stale
/// rectangle on screen, and one that reports the whole project throws away the point.
/// </remarks>
public sealed class HandlerContext
{
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);

    /// <summary>Creates a context.</summary>
    /// <param name="services">Services a handler may need, for work outside the project.</param>
    /// <param name="clock">The clock, injected so tests do not depend on the time of day.</param>
    /// <param name="projectPath">Where the project lives, or empty when it has never been saved.</param>
    public HandlerContext(IServiceProvider? services = null, TimeProvider? clock = null, string projectPath = "")
    {
        Services = services;
        Clock = clock ?? TimeProvider.System;
        ProjectPath = projectPath;
    }

    /// <summary>Services for work outside the project, such as probing a file.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>The clock. Handlers that stamp a time use this one.</summary>
    public TimeProvider Clock { get; }

    /// <summary>Where the project lives, or empty when it has never been saved.</summary>
    public string ProjectPath { get; init; }

    /// <summary>
    /// Queues a command to run through the session after this one, with an issuer, for work that
    /// finishes later: a watched folder's imports. Null outside a session.
    /// </summary>
    public Func<ICommand, string, Task<CommandResult>>? Later { get; init; }

    /// <summary>
    /// Stops a handler doing long work (an analysis, a gather, a proxy) part way: Ctrl+C in jazz,
    /// a client giving up. Such a handler passes it on and leaves nothing behind when it fires; the
    /// project is untouched, because a handler that throws commits nothing (Phase 33).
    /// </summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>
    /// What the handler's <see cref="IPreparingHandler{TCommand}.Prepare"/> worked out before the
    /// command was queued, or null when it was not prepared (inside a batch, on a replay, in a
    /// test that calls the handler itself): the handler then does that work itself.
    /// </summary>
    public object? Prepared { get; init; }

    /// <summary>Everything the handler touched, in the order it said so.</summary>
    public ImmutableArray<string> ChangedIds { get; private set; } = [];

    /// <summary>Records that something changed.</summary>
    public void Changed(string? id)
    {
        if (id is { Length: > 0 } && _changed.Add(id))
        {
            ChangedIds = ChangedIds.Add(id);
        }
    }

    /// <summary>Records that several things changed.</summary>
    public void Changed(IEnumerable<string?> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        foreach (string? id in ids)
        {
            Changed(id);
        }
    }

    /// <summary>
    /// Runs another command's handler against a project, for a handler made of other commands (a
    /// motion template): its changes are reported here, and the whole is one undo.
    /// </summary>
    public Project Run(Project project, ICommand command)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);

        Type handlerType = typeof(ICommandHandler<>).MakeGenericType(command.GetType());
        object handler = Services?.GetService(handlerType)
            ?? throw new CommandException("no-handler", $"Nothing handles '{CommandRegistry.NameOf(command)}' here.");
        try
        {
            return (Project)handlerType.GetMethod(nameof(ICommandHandler<ICommand>.Handle))!.Invoke(handler, [project, command, this])!;
        }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error.InnerException);
            throw;
        }
    }

    /// <summary>Unwraps an editing result, turning its error into a coded command failure.</summary>
    /// <remarks>
    /// <see cref="EditOps"/> reports ordinary failures as values rather than exceptions, because
    /// running out of source material is not a bug. The command layer is where that becomes an
    /// error the CLI can exit on and the UI can show.
    /// </remarks>
    public static T Require<T>(EditResult<T> result)
    {
        if (result.IsOk)
        {
            return result.Value;
        }

        throw new CommandException(result.Error!.Code, result.Error.Message);
    }
}

/// <summary>
/// A handler with a slow part that reads but does not change the project (analysing a recording,
/// hearing speech, copying files), which runs before the command is queued so the commands asked
/// for meanwhile do not wait behind it.
/// </summary>
/// <remarks>
/// <see cref="Prepare"/> runs on a worker thread against the project as it is when the command is
/// asked for, with the command's cancellation; a refusal there is the command's refusal. What it
/// returns reaches <see cref="ICommandHandler{TCommand}.Handle"/> as <see cref="HandlerContext.Prepared"/>
/// once the command's turn comes; often it fills a cache the handler then reads. Its changed ids
/// are not kept: the handler reports what changes.
/// </remarks>
/// <typeparam name="TCommand">The command type.</typeparam>
public interface IPreparingHandler<in TCommand>
    where TCommand : ICommand
{
    /// <summary>Does the slow part, off the command queue.</summary>
    object? Prepare(Project project, TCommand command, HandlerContext context);
}

/// <summary>Turns one command into a new project.</summary>
/// <typeparam name="TCommand">The command this handles.</typeparam>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    /// <summary>Applies the command, returning the project it produces.</summary>
    Project Handle(Project project, TCommand command, HandlerContext context);
}

/// <summary>Answers one query against a project.</summary>
/// <typeparam name="TQuery">The query this answers.</typeparam>
/// <typeparam name="TResult">What it returns.</typeparam>
public interface IQueryHandler<in TQuery, out TResult>
    where TQuery : IQuery<TResult>
{
    /// <summary>Answers the query. Must not change anything.</summary>
    TResult Handle(Project project, TQuery query, QueryContext context);
}

/// <summary>What a query handler is given besides the project and the query.</summary>
/// <param name="Session">The session, for the few queries that ask about it rather than the project.</param>
/// <param name="Services">
/// The session's services, for the few queries about something outside the project, such as
/// where the playhead is.
/// </param>
public sealed record QueryContext(ISessionState? Session = null, IServiceProvider? Services = null);

/// <summary>The parts of a session a query may read.</summary>
/// <remarks>
/// Narrow on purpose. A query that could reach the dispatcher could run a command, and the whole
/// point of separating the two is that a query cannot change anything.
/// </remarks>
public interface ISessionState
{
    /// <summary>Where the project lives, or empty when it has never been saved.</summary>
    string ProjectPath { get; }

    /// <summary>True when there are changes the file does not have.</summary>
    bool IsDirty { get; }

    /// <summary>How many commands have been applied since the session opened.</summary>
    long Version { get; }

    /// <summary>What has been done, oldest first.</summary>
    ImmutableArray<HistoryInfo> History(int limit);

    /// <summary>What the session has noticed that a person should know about: fallbacks, missing files.</summary>
    ImmutableArray<Core.Diagnostics.Diagnostic> Diagnostics { get; }
}
