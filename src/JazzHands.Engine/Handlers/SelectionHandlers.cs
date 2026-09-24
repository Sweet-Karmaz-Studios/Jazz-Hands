using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Selection;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>What the selection handlers share.</summary>
internal static class SelectionHelp
{
    /// <summary>The session's selection. Every host registers one with the engine.</summary>
    internal static SelectionService Service(IServiceProvider? services) =>
        services?.GetService<SelectionService>()
        ?? throw new CommandException(
            "no-selection",
            "This host has no selection. Register the engine with AddJazzHandsEngine.");
}

/// <summary>Chooses what is selected.</summary>
public sealed class SetSelectionHandler : ICommandHandler<SetSelectionCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetSelectionCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        // Removing an id that is no longer there is harmless; selecting one is a mistake worth
        // naming, because the script that did it is about to act on something that is not there.
        if (command.Mode is SelectMode.Replace or SelectMode.Add or SelectMode.Toggle)
        {
            HashSet<string> present = SelectionService.Present(project);
            string[] missing = [.. command.Ids.Where(id => !present.Contains(id))];

            if (missing.Length > 0)
            {
                throw new CommandException(
                    "not-found",
                    $"Nothing in the active sequence has the id {string.Join(", ", missing.Select(id => $"'{id}'"))}. Only its clips, transitions and markers can be selected.");
            }
        }

        SelectionHelp.Service(context.Services).Set(command.Ids, command.Mode);
        return project;
    }
}

/// <summary>Selects nothing.</summary>
public sealed class ClearSelectionHandler : ICommandHandler<ClearSelectionCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ClearSelectionCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        SelectionHelp.Service(context.Services).Clear();
        return project;
    }
}

/// <summary>Says what is selected.</summary>
public sealed class GetSelectionHandler : IQueryHandler<GetSelectionQuery, SelectionInfo>
{
    /// <inheritdoc />
    public SelectionInfo Handle(Project project, GetSelectionQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        return new SelectionInfo(
            new EquatableArray<string>([.. SelectionHelp.Service(context.Services).Ids]),
            project.ActiveSequence?.Id);
    }
}
