using System.Text.Json;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using Microsoft.Data.Sqlite;
using Serilog;

namespace JazzHands.Engine.Export;

/// <summary>What the export commands reach: the queue of a running editor.</summary>
public interface IExportService
{
    /// <summary>Raised when a job is added, moves, progresses or goes. On a worker thread.</summary>
    event EventHandler<ExportJobInfo>? Changed;

    /// <summary>Adds a planned job; returns its id.</summary>
    /// <param name="plan">What to write.</param>
    /// <param name="project">The project as it is now; the job keeps this copy.</param>
    /// <param name="projectPath">Where the project lives, for relative media paths.</param>
    /// <param name="jobId">The id to give it, or null for a fresh one.</param>
    string Enqueue(ExportPlan plan, Project project, string projectPath, string? jobId = null);

    /// <summary>Stops a queued or running job. False when there is no such unfinished job.</summary>
    bool Cancel(string jobId);

    /// <summary>Takes finished jobs off the list; returns how many.</summary>
    int Clear();

    /// <summary>Every job, oldest first.</summary>
    ExportJobInfo[] List();
}

/// <summary>
/// The export queue: jobs run one at a time on a worker thread, and survive a restart.
/// </summary>
/// <remarks>
/// A job keeps the plan and a copy of the project as they were when it was queued, so editing on
/// while it waits changes nothing about the file it writes. Both are written to a SQLite file
/// (<c>%LOCALAPPDATA%\JazzHands\queue.db</c>) as they change state, and a job that was running
/// when the editor stopped is queued again when it starts: a half-written export was deleted, so
/// the only way to finish it is from the top.
///
/// Progress is kept in memory and raised about four times a second; the database hears only about
/// changes of state, which is what a restart needs.
///
/// One worker, so one job runs at a time. That is v1: NVENC on the 4090 can run several sessions,
/// and Phase 22 lets the queue use them.
/// </remarks>
public sealed class ExportQueue : IExportService, IDisposable
{
    private readonly ILogger _log = Log.ForContext<ExportQueue>();
    private readonly SqliteConnection _database;
    private readonly ExportEnvironment _environment;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private bool _busy;
    private readonly List<Job> _jobs = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private Thread? _worker;
    private bool _disposed;

    /// <summary>Opens the queue, reading back the jobs a previous run left.</summary>
    /// <param name="databasePath">The queue file. Defaults to the per-user one.</param>
    /// <param name="environment">Which device exports render on.</param>
    /// <param name="clock">The clock, for job times.</param>
    public ExportQueue(string? databasePath = null, ExportEnvironment? environment = null, TimeProvider? clock = null)
    {
        DatabasePath = databasePath ?? DefaultPath;
        _environment = environment ?? ExportEnvironment.Default;
        _clock = clock ?? TimeProvider.System;

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _database.Open();

        Execute("""
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                position INTEGER NOT NULL,
                state TEXT NOT NULL,
                plan TEXT NOT NULL,
                project TEXT NOT NULL,
                project_path TEXT NOT NULL,
                created TEXT NOT NULL,
                finished TEXT,
                encoder TEXT,
                error TEXT,
                note TEXT
            )
            """);

        Load();
    }

    /// <inheritdoc />
    public event EventHandler<ExportJobInfo>? Changed;

    /// <summary>The per-user queue file.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JazzHands",
        "queue.db");

    /// <summary>Where this queue is kept.</summary>
    public string DatabasePath { get; }

    /// <summary>True while a job is running.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Any(job => job.State == ExportJobState.Running);
            }
        }
    }

    /// <summary>Starts the worker. Jobs queued before this wait until it is called.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_worker is not null)
        {
            return;
        }

        _worker = new Thread(Work) { IsBackground = true, Name = "Jazz export queue" };
        _worker.Start();
        _wake.Release();
    }

    /// <inheritdoc />
    public string Enqueue(ExportPlan plan, Project project, string projectPath, string? jobId = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(project);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var job = new Job(jobId ?? Id.New(), plan, project, projectPath ?? string.Empty, _clock.GetUtcNow());

        lock (_gate)
        {
            if (_jobs.Any(existing => string.Equals(existing.Id, job.Id, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"There is already a job '{job.Id}'.", nameof(jobId));
            }

            job.Position = _jobs.Count == 0 ? 0 : _jobs.Max(existing => existing.Position) + 1;
            _jobs.Add(job);
            Insert(job);
        }

        _log.Information("Queued export {Job} of {Output} ({Mode})", job.Id, plan.OutputPath, plan.Mode);
        Raise(job);
        _wake.Release();
        return job.Id;
    }

    /// <inheritdoc />
    public bool Cancel(string jobId)
    {
        Job? job;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(candidate => string.Equals(candidate.Id, jobId, StringComparison.Ordinal));
            if (job is null || job.IsFinished)
            {
                return false;
            }

            if (job.State == ExportJobState.Queued)
            {
                job.State = ExportJobState.Cancelled;
                job.Finished = _clock.GetUtcNow();
                Update(job);
            }
            else
            {
                job.Cancellation.Cancel();
            }
        }

        Raise(job);
        return true;
    }

    /// <inheritdoc />
    public int Clear()
    {
        Job[] gone;
        lock (_gate)
        {
            gone = [.. _jobs.Where(job => job.IsFinished)];
            foreach (Job job in gone)
            {
                _jobs.Remove(job);
                Execute("DELETE FROM jobs WHERE id = $id", ("$id", job.Id));
            }
        }

        return gone.Length;
    }

    /// <inheritdoc />
    public ExportJobInfo[] List()
    {
        lock (_gate)
        {
            return [.. _jobs.OrderBy(job => job.Position).Select(job => job.Describe())];
        }
    }

    /// <summary>Waits until nothing is queued or running, for tests and the CLI.</summary>
    public bool WaitUntilIdle(TimeSpan timeout)
    {
        DateTime until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            lock (_gate)
            {
                if (!_busy && _jobs.All(job => job.IsFinished))
                {
                    return true;
                }
            }

            Thread.Sleep(20);
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        lock (_gate)
        {
            foreach (Job job in _jobs)
            {
                job.Cancellation.Cancel();
            }
        }

        _wake.Release();
        _worker?.Join();

        _database.Dispose();
        _wake.Dispose();
        _shutdown.Dispose();
    }

    private void Work()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            Job? next;
            lock (_gate)
            {
                next = _jobs.Where(job => job.State == ExportJobState.Queued).OrderBy(job => job.Position).FirstOrDefault();
                if (next is not null)
                {
                    next.State = ExportJobState.Running;
                    _busy = true;
                    Update(next);
                }
            }

            if (next is null)
            {
                try
                {
                    _wake.Wait(_shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            Raise(next);
            RunOne(next);

            // Only now, with the last Changed delivered, is the queue idle: a listener that
            // waited for idle and then read its own state would otherwise still see Running.
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    private void RunOne(Job job)
    {
        var progress = new Exporter.Synchronous<ExportProgress>(step =>
        {
            job.Progress = step;
            job.Encoder = step.Encoder ?? job.Encoder;
            Raise(job);
        });

        try
        {
            ExportResult result = Exporter.Run(job.Plan, job.Project, job.ProjectPath, _environment, progress, job.Cancellation.Token);
            lock (_gate)
            {
                job.State = ExportJobState.Done;
                job.Encoder = result.Encoder;
                job.Bytes = result.Bytes;
                job.Note = Join(job.Note, result.Notes.Count == 0 ? null : string.Join(" ", result.Notes));
            }
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            lock (_gate)
            {
                // Stopped by the editor closing rather than by a person: run it again next time.
                job.State = _shutdown.IsCancellationRequested ? ExportJobState.Queued : ExportJobState.Cancelled;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The top of the worker: anything a job throws fails that job and not the queue.
            _log.Error(error, "Export {Job} of {Output} failed", job.Id, job.Plan.OutputPath);
            lock (_gate)
            {
                job.State = ExportJobState.Failed;
                job.Error = error.Message;
            }
        }

        lock (_gate)
        {
            job.Finished = job.State == ExportJobState.Queued ? null : _clock.GetUtcNow();
            if (!_disposed)
            {
                Update(job);
            }
        }

        Raise(job);
    }

    private void Load()
    {
        using SqliteCommand command = _database.CreateCommand();
        command.CommandText = "SELECT id, position, state, plan, project, project_path, created, finished, encoder, error, note FROM jobs ORDER BY position";
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            string id = reader.GetString(0);
            try
            {
                ExportPlan plan = JsonSerializer.Deserialize<ExportPlan>(reader.GetString(3), JazzJson.Options)!;
                string projectPath = reader.GetString(5);
                Project project = ProjectFile.Parse(reader.GetString(4), projectPath.Length > 0 ? projectPath : "queued.jazz").Project;

                var job = new Job(id, plan, project, projectPath, DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture))
                {
                    Position = reader.GetInt64(1),
                    State = Enum.Parse<ExportJobState>(reader.GetString(2)),
                    Finished = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
                    Encoder = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Error = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Note = reader.IsDBNull(10) ? null : reader.GetString(10),
                };

                if (job.State == ExportJobState.Running)
                {
                    // It was running when the editor stopped. The partial file is gone; start again.
                    job.State = ExportJobState.Queued;
                    job.Note = Join(job.Note, "Restarted: Jazz Hands closed while it was exporting.");
                }

                _jobs.Add(job);
            }
            catch (Exception error) when (error is JsonException or ProjectFileException or FormatException or ArgumentException)
            {
                _log.Warning(error, "Dropping export job {Job}, which this build cannot read", id);
                Execute("DELETE FROM jobs WHERE id = $id", ("$id", id));
            }
        }

        foreach (Job job in _jobs.Where(job => job.State == ExportJobState.Queued))
        {
            Update(job);
        }
    }

    private void Insert(Job job) => Execute(
        """
        INSERT INTO jobs (id, position, state, plan, project, project_path, created, finished, encoder, error, note)
        VALUES ($id, $position, $state, $plan, $project, $path, $created, NULL, NULL, NULL, NULL)
        """,
        ("$id", job.Id),
        ("$position", job.Position),
        ("$state", job.State.ToString()),
        ("$plan", JsonSerializer.Serialize(job.Plan, JazzJson.Options)),
        ("$project", ProjectFile.Render(job.Project)),
        ("$path", job.ProjectPath),
        ("$created", job.Created.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));

    private void Update(Job job) => Execute(
        "UPDATE jobs SET state = $state, finished = $finished, encoder = $encoder, error = $error, note = $note WHERE id = $id",
        ("$id", job.Id),
        ("$state", job.State.ToString()),
        ("$finished", (object?)job.Finished?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
        ("$encoder", job.Encoder),
        ("$error", job.Error),
        ("$note", job.Note));

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = _database.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private void Raise(Job job)
    {
        ExportJobInfo info;
        lock (_gate)
        {
            info = job.Describe();
        }

        try
        {
            Changed?.Invoke(this, info);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A panel with a bug in its redraw must not stop an export.
            _log.Error(error, "An export queue listener threw");
        }
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first} {second}";

    /// <summary>One job and everything the queue knows about it.</summary>
    private sealed class Job(string id, ExportPlan plan, Project project, string projectPath, DateTimeOffset created)
    {
        public string Id { get; } = id;

        public ExportPlan Plan { get; } = plan;

        public Project Project { get; } = project;

        public string ProjectPath { get; } = projectPath;

        public DateTimeOffset Created { get; } = created;

        public long Position { get; set; }

        public ExportJobState State { get; set; } = ExportJobState.Queued;

        public ExportProgress Progress { get; set; }

        public DateTimeOffset? Finished { get; set; }

        public string? Encoder { get; set; }

        public string? Error { get; set; }

        public string? Note { get; set; }

        public long Bytes { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public bool IsFinished => State is ExportJobState.Done or ExportJobState.Failed or ExportJobState.Cancelled;

        public ExportJobInfo Describe()
        {
            double? eta = Progress.Fps > 0 && Progress.TotalFrames > 0
                ? (Progress.TotalFrames - Progress.Frame) / Progress.Fps
                : null;

            string? snaps = Plan.Snaps.IsEmpty ? null : $"{Plan.Snaps.Length} cut(s) moved to keyframes.";

            return new ExportJobInfo(
                Id,
                State,
                Plan.OutputPath,
                Plan.Preset,
                Plan.Mode,
                State == ExportJobState.Done ? 1.0 : Progress.Fraction,
                Progress.Frame,
                Progress.TotalFrames,
                Progress.Fps,
                State == ExportJobState.Running ? eta : null,
                Bytes > 0 ? Bytes : Progress.Bytes,
                Encoder,
                Error,
                Created,
                Finished,
                Join(snaps, Note));
        }
    }
}
