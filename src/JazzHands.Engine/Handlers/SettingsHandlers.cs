using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>Changes a setting in the settings file; the project is untouched.</summary>
public sealed class SetSettingHandler : ICommandHandler<SetSettingCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetSettingCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        context.Services!.GetRequiredService<SettingsCatalog>().Set(command.Key, command.Value);
        return project;
    }
}

/// <summary>Lists the settings.</summary>
public sealed class GetSettingsHandler : IQueryHandler<GetSettingsQuery, SettingInfo[]>
{
    /// <inheritdoc />
    public SettingInfo[] Handle(Project project, GetSettingsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        return context.Services!.GetRequiredService<SettingsCatalog>().List(query.Section);
    }
}
