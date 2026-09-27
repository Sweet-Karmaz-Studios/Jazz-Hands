using JazzHands.Core;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Recovery;

namespace JazzHands.Engine.Handlers;

/// <summary>Answers <c>recovery.check</c> for the session's project and the rescued untitled ones.</summary>
public sealed class CheckRecoveryHandler : IQueryHandler<CheckRecoveryQuery, RecoveryInfo>
{
    /// <inheritdoc />
    public RecoveryInfo Handle(Project project, CheckRecoveryQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var service = new RecoveryService();
        UntitledRecoveryInfo[] untitled = [.. service.FindUntitled().Select(item => new UntitledRecoveryInfo(item.Path, item.Name, item.SavedAt))];
        string path = context.Session?.ProjectPath ?? string.Empty;

        if (path.Length == 0 || service.Find(path) is not { } offer)
        {
            string nothing = untitled.Length == 0
                ? "There is nothing to recover."
                : $"There is nothing to recover for this project; {Words.Count(untitled.Length, "project")} that {(untitled.Length == 1 ? "was" : "were")} never saved {(untitled.Length == 1 ? "was" : "were")} rescued.";
            return new RecoveryInfo(false, path, nothing, null, 0, false, untitled);
        }

        return new RecoveryInfo(true, offer.ProjectPath, offer.Describe(), offer.RecoveredAt, (offer.History ?? offer.CommandsAfterRecovery).Count, offer.HasCopy, untitled);
    }
}

/// <summary>
/// <c>recovery.accept</c> belongs to the session, which replaces its project with the recovered
/// one as it does for <c>project.open</c>; inside a batch there is no session to replace it in.
/// </summary>
public sealed class AcceptRecoveryHandler : ICommandHandler<AcceptRecoveryCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, AcceptRecoveryCommand command, HandlerContext context) =>
        throw new CommandException("session-command", "recovery.accept replaces the project, so it cannot be part of a batch. Run it on its own.");
}

/// <summary><c>recovery.discard</c> belongs to the session too; see <see cref="AcceptRecoveryHandler"/>.</summary>
public sealed class DiscardRecoveryHandler : ICommandHandler<DiscardRecoveryCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, DiscardRecoveryCommand command, HandlerContext context) =>
        throw new CommandException("session-command", "recovery.discard works on the session's files, so it cannot be part of a batch. Run it on its own.");
}
