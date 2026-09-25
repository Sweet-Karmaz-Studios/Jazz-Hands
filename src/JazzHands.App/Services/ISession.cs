using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;

namespace JazzHands.App.Services;

/// <summary>
/// What a viewmodel is allowed to do with the engine.
/// </summary>
/// <remarks>
/// Read the project, run a command, run a query, hear about changes. Nothing else, and in
/// particular no way to mutate the model: a viewmodel that wants something changed dispatches the
/// command for it, the same command the CLI and the control server dispatch.
///
/// The seam also makes viewmodels testable without an engine. Tests use a fake that records what
/// was dispatched and asserts on the commands, which is the behaviour that matters, rather than
/// on whatever the UI did with the result.
/// </remarks>
public interface ISession
{
    /// <summary>Raised after every command, on an engine thread.</summary>
    event EventHandler<ProjectChangedEventArgs>? ProjectChanged;

    /// <summary>The current project. An immutable snapshot, safe to hold and compare.</summary>
    Project Project { get; }

    /// <summary>Where the project lives, or empty when it has never been saved.</summary>
    string ProjectPath { get; }

    /// <summary>True when there are changes since the last save.</summary>
    bool IsDirty { get; }

    /// <summary>Runs a command through the dispatcher.</summary>
    Task<CommandResult> ExecuteAsync(ICommand command, CancellationToken cancellationToken = default);

    /// <summary>Answers a question about the project without changing anything.</summary>
    TResult Query<TResult>(IQuery<TResult> query);
}

/// <summary>The engine session, presented to the UI.</summary>
/// <param name="session">The session this wraps. Owned by the host, not by this adapter.</param>
public sealed class EngineSession(Session session) : ISession
{
    /// <inheritdoc />
    public event EventHandler<ProjectChangedEventArgs>? ProjectChanged
    {
        add => session.ProjectChanged += value;
        remove => session.ProjectChanged -= value;
    }

    /// <inheritdoc />
    public Project Project => session.Project;

    /// <inheritdoc />
    public string ProjectPath => session.ProjectPath;

    /// <inheritdoc />
    public bool IsDirty => session.IsDirty;

    /// <inheritdoc />
    public Task<CommandResult> ExecuteAsync(ICommand command, CancellationToken cancellationToken = default) =>
        session.ExecuteAsync(command, cancellationToken);

    /// <inheritdoc />
    public TResult Query<TResult>(IQuery<TResult> query) => session.Query(query);
}
