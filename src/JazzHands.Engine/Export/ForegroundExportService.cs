using System.Collections.Concurrent;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Engine.Export;

/// <summary>
/// The export "queue" of a headless process: a job runs as it is queued, in the foreground, and
/// the command that queued it returns when the file is written.
/// </summary>
/// <remarks>
/// A headless jazz process is gone when its command is done, so a queue there would never run.
/// With this in place, <c>export.enqueue</c> and <c>export.batch</c> in a <c>jazz apply</c>
/// script, or typed on their own, write their files before the next step, which is what a script
/// means by them. A failed export fails the command, so the script stops there. Hooks (show the
/// folder, run a script) belong to the editor's queue and are not run here.
/// </remarks>
public sealed class ForegroundExportService : IExportService
{
    private readonly ILogger _log = Serilog.Log.ForContext<ForegroundExportService>();
    private readonly ConcurrentDictionary<string, ExportJobInfo> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string[]> _logs = new(StringComparer.Ordinal);
    private readonly ExportEnvironment _environment;

    /// <summary>Creates one that renders on the given environment, or the default.</summary>
    public ForegroundExportService(ExportEnvironment? environment = null) => _environment = environment ?? ExportEnvironment.Default;

    /// <inheritdoc />
    public event EventHandler<ExportJobInfo>? Changed;

    /// <inheritdoc />
    public string Enqueue(ExportPlan plan, Project project, string projectPath, string? jobId = null, ExportJobOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string id = jobId ?? Id.New();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var job = new ExportJobInfo(id, ExportJobState.Running, plan.OutputPath, plan.Preset, plan.Mode, 0, 0, 0, 0, null, 0, null, null, started, null);
        Raise(job);

        try
        {
            ExportResult result = Exporter.Run(plan, project, projectPath, _environment);
            _logs[id] = [.. result.Notes, $"Wrote {result.Path}: {result.Bytes} bytes with {result.Encoder} in {result.Elapsed.TotalSeconds:F1} s."];
            Raise(job with { State = ExportJobState.Done, Progress = 1, Bytes = result.Bytes, Encoder = result.Encoder, Finished = DateTimeOffset.UtcNow });
            return id;
        }
        catch (Exception error) when (error is ExportException or FfmpegException or IOException or InvalidOperationException or CommandException)
        {
            _log.Warning(error, "Foreground export {Path} failed", plan.OutputPath);
            _logs[id] = [error.Message];
            Raise(job with { State = ExportJobState.Failed, Error = error.Message, Finished = DateTimeOffset.UtcNow });
            throw error as CommandException ?? new CommandException("export-failed", $"Exporting '{plan.OutputPath}' failed: {error.Message}");
        }
    }

    /// <inheritdoc />
    public bool Cancel(string jobId) => false;

    /// <inheritdoc />
    public int Pause(string? jobId) => 0;

    /// <inheritdoc />
    public int Resume(string? jobId) => 0;

    /// <inheritdoc />
    public bool SetPriority(string jobId, ExportPriority priority) => false;

    /// <inheritdoc />
    public string[]? Log(string jobId) => _logs.TryGetValue(jobId, out string[]? lines) ? lines : null;

    /// <inheritdoc />
    public int Clear()
    {
        int count = _jobs.Count;
        _jobs.Clear();
        _logs.Clear();
        return count;
    }

    /// <inheritdoc />
    public ExportJobInfo[] List() => [.. _jobs.Values.OrderBy(job => job.Created)];

    private void Raise(ExportJobInfo job)
    {
        _jobs[job.Id] = job;
        Changed?.Invoke(this, job);
    }
}
