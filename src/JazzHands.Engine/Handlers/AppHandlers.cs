using JazzHands.Core.Commands;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>Finds the editor for the <c>app</c> commands.</summary>
internal static class AppHelp
{
    /// <summary>Runs an action against the editor and hands the project back unchanged.</summary>
    internal static Project Drive(Project project, HandlerContext context, Action<IAppController> action)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);

        IAppController app = context.Services?.GetService<IAppController>()
            ?? throw new CommandException(
                "no-app",
                "There is no editor window in a headless session. Start Jazz Hands, or send the command to one with --attach.");
        action(app);
        return project;
    }
}

/// <summary>Shows the window.</summary>
public sealed class ShowAppHandler : ICommandHandler<ShowAppCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ShowAppCommand command, HandlerContext context) =>
        AppHelp.Drive(project, context, app => app.Show());
}

/// <summary>Hides the window to the notification area.</summary>
public sealed class HideAppHandler : ICommandHandler<HideAppCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, HideAppCommand command, HandlerContext context) =>
        AppHelp.Drive(project, context, app => app.Hide());
}

/// <summary>Quits the editor.</summary>
public sealed class QuitAppHandler : ICommandHandler<QuitAppCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, QuitAppCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AppHelp.Drive(project, context, app => app.Quit(command.Force, command.WaitForExports));
    }
}

/// <summary>Takes the editor's registration back from Windows.</summary>
public sealed class UnregisterAppHandler : ICommandHandler<UnregisterAppCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, UnregisterAppCommand command, HandlerContext context) =>
        AppHelp.Drive(project, context, app => app.Unregister());
}
