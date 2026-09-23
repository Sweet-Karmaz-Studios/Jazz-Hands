using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Export;
using Microsoft.Extensions.DependencyInjection;

namespace JazzHands.Engine.Handlers;

/// <summary>Finds the export queue and the keyframe indexes for the export commands.</summary>
internal static class ExportHelp
{
    /// <summary>The queue, or a coded refusal in a process without one.</summary>
    internal static IExportService Queue(IServiceProvider? services) =>
        services?.GetService<IExportService>()
        ?? throw new CommandException(
            "no-export-queue",
            "The export queue belongs to a running editor, and this is a headless session. Export in the foreground with 'jazz export' or 'jazz trim', or send the command to a running editor with --attach.");

    /// <summary>The shared keyframe lookup, or one for this call when the host registered none.</summary>
    internal static KeyframeLookup Keyframes(IServiceProvider? services) =>
        services?.GetService<KeyframeLookup>() ?? new KeyframeLookup(services?.GetService<Media.Import.CacheManager>());
}

/// <summary>Plans an export against the project as it is and queues it.</summary>
public sealed class EnqueueExportHandler : ICommandHandler<EnqueueExportCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, EnqueueExportCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        IExportService queue = ExportHelp.Queue(context.Services);
        string id = HandlerHelp.IdOr(command.JobId);

        ExportPlan plan = ExportPlanner.Plan(project, context.ProjectPath, command.ToRequest(), ExportHelp.Keyframes(context.Services));

        if (queue.List().Any(job => !job.IsFinished && string.Equals(job.OutputPath, plan.OutputPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CommandException(
                "output-in-use",
                $"Another export in the queue is already writing '{plan.OutputPath}'. Pick another name, or cancel that one.");
        }

        queue.Enqueue(plan, project, context.ProjectPath, id);
        context.Changed(id);
        return project;
    }
}

/// <summary>Stops a queued or running export.</summary>
public sealed class CancelExportHandler : ICommandHandler<CancelExportCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, CancelExportCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (!ExportHelp.Queue(context.Services).Cancel(command.JobId))
        {
            throw new CommandException(
                "export-job-not-found",
                $"There is no queued or running export '{command.JobId}'. 'jazz export list' shows the queue.");
        }

        context.Changed(command.JobId);
        return project;
    }
}

/// <summary>Takes finished jobs off the queue.</summary>
public sealed class ClearExportsHandler : ICommandHandler<ClearExportsCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ClearExportsCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ExportHelp.Queue(context.Services).Clear();
        return project;
    }
}

/// <summary>Plans an export without running it.</summary>
public sealed class PlanExportHandler : IQueryHandler<PlanExportQuery, ExportPlan>
{
    /// <inheritdoc />
    public ExportPlan Handle(Project project, PlanExportQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        return ExportPlanner.Plan(
            project,
            context.Session?.ProjectPath ?? string.Empty,
            query.ToRequest(),
            ExportHelp.Keyframes(context.Services));
    }
}

/// <summary>Lists the export queue. A headless session has none, and so lists nothing.</summary>
public sealed class ListExportsHandler : IQueryHandler<ListExportsQuery, ExportJobInfo[]>
{
    /// <inheritdoc />
    public ExportJobInfo[] Handle(Project project, ListExportsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Services?.GetService<IExportService>()?.List() ?? [];
    }
}

/// <summary>Lists the export presets.</summary>
public sealed class ListExportPresetsHandler : IQueryHandler<ListExportPresetsQuery, ExportPresetInfo[]>
{
    /// <inheritdoc />
    public ExportPresetInfo[] Handle(Project project, ListExportPresetsQuery query, QueryContext context) =>
        [.. ExportPresets.All];
}
