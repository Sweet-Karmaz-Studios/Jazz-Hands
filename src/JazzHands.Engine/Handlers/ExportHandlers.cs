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

        queue.Enqueue(plan, project, context.ProjectPath, id, command.ToOptions());
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

/// <summary>Holds an export, or all of them.</summary>
public sealed class PauseExportHandler : ICommandHandler<PauseExportCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, PauseExportCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        IExportService queue = ExportHelp.Queue(context.Services);
        if (queue.Pause(command.JobId) == 0 && command.JobId is { } id)
        {
            throw new CommandException(
                "export-job-not-found",
                $"There is no waiting or running export '{id}' to pause. 'jazz export list' shows the queue.");
        }

        if (command.JobId is { } paused)
        {
            context.Changed(paused);
        }

        return project;
    }
}

/// <summary>Lets a paused export run again, or all of them.</summary>
public sealed class ResumeExportHandler : ICommandHandler<ResumeExportCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ResumeExportCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        IExportService queue = ExportHelp.Queue(context.Services);
        if (queue.Resume(command.JobId) == 0 && command.JobId is { } id)
        {
            throw new CommandException(
                "export-job-not-found",
                $"There is no paused export '{id}'. 'jazz export list' shows the queue.");
        }

        if (command.JobId is { } resumed)
        {
            context.Changed(resumed);
        }

        return project;
    }
}

/// <summary>Moves an export up or down the queue.</summary>
public sealed class SetExportPriorityHandler : ICommandHandler<SetExportPriorityCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SetExportPriorityCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        if (!ExportHelp.Queue(context.Services).SetPriority(command.JobId, command.Priority))
        {
            throw new CommandException(
                "export-job-not-found",
                $"There is no unfinished export '{command.JobId}'. 'jazz export list' shows the queue.");
        }

        context.Changed(command.JobId);
        return project;
    }
}

/// <summary>What an export has done, step by step.</summary>
public sealed class ExportLogHandler : IQueryHandler<ExportLogQuery, string[]>
{
    /// <inheritdoc />
    public string[] Handle(Project project, ExportLogQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        return ExportHelp.Queue(context.Services).Log(query.JobId)
            ?? throw new CommandException(
                "export-job-not-found",
                $"There is no export '{query.JobId}'. 'jazz export list' shows the queue.");
    }
}

/// <summary>Writes the frame at a time as a PNG, straight away.</summary>
public sealed class ExportStillHandler : ICommandHandler<ExportStillCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ExportStillCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ExportResult result = StillExport.Still(
            project,
            context.ProjectPath,
            command.Output,
            command.At,
            command.SequenceId,
            command.Size,
            context.Services?.GetService<ExportEnvironment>());
        context.Changed(result.Path);
        return project;
    }
}

/// <summary>Writes a contact sheet, straight away.</summary>
public sealed class ContactSheetHandler : ICommandHandler<ContactSheetCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, ContactSheetCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        Frames.StillRenderer? shared = context.Services?.GetService<Frames.StillRenderer>();
        using Frames.StillRenderer? own = shared is null ? new Frames.StillRenderer() : null;
        ContactSheetResult sheet = StillExport.ContactSheet(
            project,
            context.ProjectPath,
            command.Output,
            shared ?? own!,
            command.Columns,
            command.Rows,
            command.Width,
            command.SequenceId,
            ExportOverrideText.Range(command.Start, command.End),
            fractions: command.Times.IsEmpty ? null : [.. command.Times],
            clipId: command.ClipId);
        context.Changed(sheet.Path);
        return project;
    }
}

/// <summary>Plans an export for each range marker or stretch and queues them all, or none.</summary>
public sealed class BatchExportHandler : ICommandHandler<BatchExportCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, BatchExportCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        IExportService queue = ExportHelp.Queue(context.Services);
        Sequence sequence = (command.SequenceId is { } id ? project.Sequence(id) : project.ActiveSequence)
            ?? throw new CommandException("sequence-not-found", command.SequenceId is null ? "The project has no sequence to export." : $"No sequence with id '{command.SequenceId}'.");
        ExportPreset preset = ExportPresetLibrary.Require(command.Preset);
        KeyframeLookup keyframes = ExportHelp.Keyframes(context.Services);

        // Every one planned first, so a refusal stops the batch before anything is queued.
        ExportPlan[] plans = [.. ExportBatch.Items(sequence, command.Markers, command.Ranges, command.NameContains)
            .Select(item => ExportPlanner.Plan(
                project,
                context.ProjectPath,
                new ExportRequest(Path.Combine(command.Folder, item.Name + preset.Extension), preset.Name, command.Mode, sequence.Id, SnapToKeyframes: true, Range: item.Range),
                keyframes))];

        string[] busy = [.. queue.List().Where(job => !job.IsFinished).Select(job => job.OutputPath)];
        if (plans.FirstOrDefault(plan => busy.Contains(plan.OutputPath, StringComparer.OrdinalIgnoreCase)) is { } clash)
        {
            throw new CommandException("output-in-use", $"Another export in the queue is already writing '{clash.OutputPath}'.");
        }

        foreach (ExportPlan plan in plans)
        {
            context.Changed(queue.Enqueue(plan, project, context.ProjectPath, options: new ExportJobOptions(command.Priority)));
        }

        return project;
    }
}
