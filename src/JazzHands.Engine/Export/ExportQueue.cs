using System.Globalization;
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
    /// <param name="options">Its priority and what to do when it is done; null for the defaults.</param>
    string Enqueue(ExportPlan plan, Project project, string projectPath, string? jobId = null, ExportJobOptions? options = null);

    /// <summary>Stops a queued, paused or running job. False when there is no such unfinished job.</summary>
    bool Cancel(string jobId);

    /// <summary>Holds a job, or with no id every unfinished one; a running job stops and starts again from the top when resumed. Returns how many were paused.</summary>
    int Pause(string? jobId);

    /// <summary>Lets a paused job, or with no id every paused one, run again. Returns how many were resumed.</summary>
    int Resume(string? jobId);

    /// <summary>Moves a job that has not started up or down the queue. False when there is no such unfinished job.</summary>
    bool SetPriority(string jobId, ExportPriority priority);

    /// <summary>What a job has done, a line a step, or null when there is no such job.</summary>
    string[]? Log(string jobId);

    /// <summary>Takes finished jobs off the list; returns how many.</summary>
    int Clear();

    /// <summary>Every job, oldest first.</summary>
    ExportJobInfo[] List();
}

/// <summary>How many exports run at once.</summary>
/// <param name="Hardware">Jobs encoding on NVENC at once. A GeForce allows several sessions; three leaves one for anything else on the machine.</param>
/// <param name="Software">Jobs encoding on the CPU at once, or 0 for one per eight logical processors.</param>
public sealed record ExportQueueLimits(int Hardware = 3, int Software = 0)
{
    /// <summary>The software limit in effect.</summary>
    public int SoftwareSlots => Software > 0 ? Software : Math.Max(1, Environment.ProcessorCount / 8);
}

/// <summary>What the queue does when a job is done and asked for more.</summary>
public interface IExportHooks
{
    /// <summary>Shows the file in Explorer.</summary>
    void OpenFolder(string path);

    /// <summary>Runs a script with the file's path as its one argument, waiting for it, and says what it printed.</summary>
    void RunScript(string script, string path, Action<string> log, CancellationToken cancellationToken);
}

/// <summary>
/// The export queue: jobs run side by side within limits, in priority order, and survive a restart.
/// </summary>
/// <remarks>
/// <para>
/// A job keeps the plan and a copy of the project as they were when it was queued, so editing on
/// while it waits changes nothing about the file it writes. Both are written to a SQLite file
/// (<c>%LOCALAPPDATA%\JazzHands\queue.db</c>) as they change state, with the job's options and log,
/// and a job that was running when the editor stopped is queued again when it starts: a
/// half-written export was deleted, so the only way to finish it is from the top.
/// </para>
/// <para>
/// A job is hardware when its plan encodes with NVENC first, and software otherwise (a copy, an
/// encode on the CPU, ffmpeg.exe). Up to <see cref="ExportQueueLimits.Hardware"/> hardware jobs
/// and <see cref="ExportQueueLimits.SoftwareSlots"/> software ones run at once, each on a thread of
/// its own. A hardware job with every NVENC slot busy starts on its software encoder if a software
/// slot is free and its chain has one, and says so, rather than waiting.
/// </para>
/// <para>
/// High priority jobs start before normal ones, normal before low, and jobs of one priority in the
/// order they were queued. Pausing a job that has not started holds it; pausing one that is
/// running stops it and deletes its partial file, and it starts again from the top when resumed.
/// </para>
/// <para>
/// Progress is kept in memory and raised about four times a second; the database hears about
/// changes of state, which is what a restart needs.
/// </para>
/// </remarks>
public sealed class ExportQueue : IExportService, IDisposable
{
    private const int LogLimit = 200;

    private readonly ILogger _log = Serilog.Log.ForContext<ExportQueue>();
    private readonly SqliteConnection _database;
    private readonly ExportEnvironment _environment;
    private readonly TimeProvider _clock;
    private readonly ExportQueueLimits _limits;
    private readonly IExportHooks _hooks;
    private readonly Lock _gate = new();
    private readonly List<Job> _jobs = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private Thread? _dispatcher;
    private bool _disposed;

    /// <summary>Opens the queue, reading back the jobs a previous run left.</summary>
    /// <param name="databasePath">The queue file. Defaults to the per-user one.</param>
    /// <param name="environment">Which device exports render on.</param>
    /// <param name="clock">The clock, for job times.</param>
    /// <param name="limits">How many jobs run at once.</param>
    /// <param name="hooks">What runs when a job is done; Explorer and the shell by default.</param>
    public ExportQueue(
        string? databasePath = null,
        ExportEnvironment? environment = null,
        TimeProvider? clock = null,
        ExportQueueLimits? limits = null,
        IExportHooks? hooks = null)
    {
        DatabasePath = databasePath ?? DefaultPath;
        _environment = environment ?? ExportEnvironment.Default;
        _clock = clock ?? TimeProvider.System;
        _limits = limits ?? new ExportQueueLimits();
        _hooks = hooks ?? new ShellExportHooks();

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

        // Columns the first queue did not have, added to a file it wrote.
        AddColumn("options", "TEXT");
        AddColumn("log", "TEXT");
        AddColumn("bytes", "INTEGER");

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

    /// <summary>The limits it runs within.</summary>
    public ExportQueueLimits Limits => _limits;

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

    /// <summary>Starts running jobs. Jobs queued before this wait until it is called.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dispatcher is not null)
        {
            return;
        }

        _dispatcher = new Thread(Dispatch) { IsBackground = true, Name = "Jazz export queue" };
        _dispatcher.Start();
        _wake.Release();
    }

    /// <inheritdoc />
    public string Enqueue(ExportPlan plan, Project project, string projectPath, string? jobId = null, ExportJobOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(project);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var job = new Job(jobId ?? Id.New(), plan, project, projectPath ?? string.Empty, _clock.GetUtcNow(), options ?? new ExportJobOptions());

        lock (_gate)
        {
            if (_jobs.Any(existing => string.Equals(existing.Id, job.Id, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"There is already a job '{job.Id}'.", nameof(jobId));
            }

            job.Position = _jobs.Count == 0 ? 0 : _jobs.Max(existing => existing.Position) + 1;
            Note(job, $"Queued: {plan.Mode.ToString().ToLowerInvariant()} with {plan.Preset} to {plan.OutputPath}, {job.Options.Priority.ToString().ToLowerInvariant()} priority.");
            _jobs.Add(job);
            Insert(job);
        }

        _log.Information("Queued export {Job} of {Output} ({Mode}, {Priority})", job.Id, plan.OutputPath, plan.Mode, job.Options.Priority);
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
            job = Find(jobId);
            if (job is null || job.IsFinished)
            {
                return false;
            }

            if (job.State is ExportJobState.Queued or ExportJobState.Paused)
            {
                job.State = ExportJobState.Cancelled;
                job.Finished = _clock.GetUtcNow();
                Note(job, "Cancelled before it ran.");
                Update(job);
            }
            else
            {
                job.PauseRequested = false;
                job.Cancellation.Cancel();
            }
        }

        Raise(job);
        return true;
    }

    /// <inheritdoc />
    public int Pause(string? jobId)
    {
        var changed = new List<Job>();
        lock (_gate)
        {
            foreach (Job job in Select(jobId).Where(job => job.State is ExportJobState.Queued or ExportJobState.Running))
            {
                if (job.State == ExportJobState.Queued)
                {
                    job.State = ExportJobState.Paused;
                    Note(job, "Paused.");
                    Update(job);
                }
                else
                {
                    // It stops where it is; the worker marks it paused when it has let go.
                    job.PauseRequested = true;
                    job.Cancellation.Cancel();
                }

                changed.Add(job);
            }
        }

        changed.ForEach(Raise);
        return changed.Count;
    }

    /// <inheritdoc />
    public int Resume(string? jobId)
    {
        var changed = new List<Job>();
        lock (_gate)
        {
            foreach (Job job in Select(jobId).Where(job => job.State == ExportJobState.Paused))
            {
                job.State = ExportJobState.Queued;
                Note(job, "Resumed.");
                Update(job);
                changed.Add(job);
            }
        }

        changed.ForEach(Raise);
        if (changed.Count > 0)
        {
            _wake.Release();
        }

        return changed.Count;
    }

    /// <inheritdoc />
    public bool SetPriority(string jobId, ExportPriority priority)
    {
        Job? job;
        lock (_gate)
        {
            job = Find(jobId);
            if (job is null || job.IsFinished)
            {
                return false;
            }

            job.Options = job.Options with { Priority = priority };
            Note(job, $"Priority set to {priority.ToString().ToLowerInvariant()}.");
            Update(job);
        }

        Raise(job);
        _wake.Release();
        return true;
    }

    /// <inheritdoc />
    public string[]? Log(string jobId)
    {
        lock (_gate)
        {
            return Find(jobId) is { } job ? [.. job.Lines] : null;
        }
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

    /// <summary>Waits until nothing is queued or running, for tests and the CLI. A paused job does not count.</summary>
    public bool WaitUntilIdle(TimeSpan timeout)
    {
        DateTime until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            lock (_gate)
            {
                if (_jobs.All(job => job.IsFinished || job.State == ExportJobState.Paused) && _jobs.All(job => job.Worker is null))
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
        Thread[] workers;
        lock (_gate)
        {
            foreach (Job job in _jobs)
            {
                job.Cancellation.Cancel();
            }

            workers = [.. _jobs.Select(job => job.Worker).OfType<Thread>()];
        }

        _wake.Release();
        _dispatcher?.Join();
        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        _database.Dispose();
        _wake.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>True for a plan whose picture goes to NVENC first.</summary>
    internal static bool IsHardware(ExportPlan plan) =>
        plan is { Mode: ExportMode.Encode, External: false, Video: { } video } && video.Encoders.Length > 0 && video.Encoders[0].Contains("nvenc", StringComparison.Ordinal);

    private void Dispatch()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var started = new List<Job>();
            lock (_gate)
            {
                int hardware = _jobs.Count(job => job.State == ExportJobState.Running && job.Hardware);
                int software = _jobs.Count(job => job.State == ExportJobState.Running && !job.Hardware);

                foreach (Job job in _jobs
                    .Where(job => job.State == ExportJobState.Queued && job.Worker is null)
                    .OrderByDescending(job => job.Options.Priority)
                    .ThenBy(job => job.Position)
                    .ToList())
                {
                    ExportPlan plan = job.Plan;
                    bool onHardware = IsHardware(plan);

                    if (onHardware && hardware >= _limits.Hardware)
                    {
                        // Every NVENC slot is taken: run it on its software encoder now rather
                        // than wait, if it has one and a software slot is free.
                        if (software < _limits.SoftwareSlots && Software(plan) is { } fallback)
                        {
                            Note(job, $"Every NVENC slot was busy, so it runs on {fallback.Video!.Encoders[0]}.");
                            plan = fallback;
                            onHardware = false;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else if (!onHardware && software >= _limits.SoftwareSlots)
                    {
                        continue;
                    }

                    if (onHardware)
                    {
                        hardware++;
                    }
                    else
                    {
                        software++;
                    }

                    job.State = ExportJobState.Running;
                    job.Hardware = onHardware;
                    job.Progress = default;
                    Note(job, onHardware ? $"Started on the GPU with {string.Join(" then ", plan.Video!.Encoders)}." : $"Started{(plan.Video is { } video ? $" with {string.Join(" then ", video.Encoders)}" : plan.Mode == ExportMode.Copy ? " as a stream copy" : string.Empty)}.");
                    Update(job);

                    ExportPlan run = plan;
                    job.Worker = new Thread(() => RunOne(job, run)) { IsBackground = true, Name = $"Jazz export {job.Id}" };
                    started.Add(job);
                }
            }

            foreach (Job job in started)
            {
                Raise(job);
                job.Worker!.Start();
            }

            try
            {
                _wake.Wait(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>A hardware plan moved onto its first software encoder, or null when its chain has none.</summary>
    private static ExportPlan? Software(ExportPlan plan)
    {
        string[] software = [.. plan.Video!.Encoders.Where(encoder => !encoder.Contains("nvenc", StringComparison.Ordinal))];
        return software.Length == 0 ? null : plan with { Video = plan.Video with { Encoders = [.. software] } };
    }

    private void RunOne(Job job, ExportPlan plan)
    {
        var progress = new Exporter.Synchronous<ExportProgress>(step =>
        {
            lock (_gate)
            {
                job.Progress = step;
                job.Encoder = step.Encoder ?? job.Encoder;
            }

            Raise(job);
        });

        ExportResult? result = null;
        try
        {
            result = Exporter.Run(plan, job.Project, job.ProjectPath, _environment, progress, job.Cancellation.Token);
            lock (_gate)
            {
                job.State = ExportJobState.Done;
                job.Encoder = result.Encoder;
                job.Bytes = result.Bytes;
                job.Note = Join(job.Note, result.Notes.Count == 0 ? null : string.Join(" ", result.Notes));
                foreach (string note in result.Notes)
                {
                    Note(job, note);
                }

                Note(job, string.Create(
                    CultureInfo.InvariantCulture,
                    $"Done: {result.Bytes / 1048576.0:F1} MB with {result.Encoder} in {result.Elapsed.TotalSeconds:F1} s ({result.Speed:F1}x real time)."));
            }
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (_shutdown.IsCancellationRequested)
                {
                    // Stopped by the editor closing rather than by a person: run it again next time.
                    job.State = ExportJobState.Queued;
                    Note(job, "Stopped because Jazz Hands closed; it runs again from the top.");
                }
                else if (job.PauseRequested)
                {
                    job.State = ExportJobState.Paused;
                    Note(job, "Paused while running; the partial file was deleted, and it starts again from the top when resumed.");
                }
                else
                {
                    job.State = ExportJobState.Cancelled;
                    Note(job, "Cancelled; the partial file was deleted.");
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The top of a worker: anything a job throws fails that job and not the queue.
            _log.Error(error, "Export {Job} of {Output} failed", job.Id, job.Plan.OutputPath);
            lock (_gate)
            {
                job.State = ExportJobState.Failed;
                job.Error = error.Message;
                Note(job, $"Failed: {error.Message}");
            }
        }

        if (result is not null)
        {
            RunHooks(job, result.Path);
        }

        lock (_gate)
        {
            job.Finished = job.State is ExportJobState.Queued or ExportJobState.Paused ? null : _clock.GetUtcNow();
            job.PauseRequested = false;
            job.Hardware = false;
            if (job.Cancellation.IsCancellationRequested && !job.IsFinished)
            {
                job.Cancellation.Dispose();
                job.Cancellation = new CancellationTokenSource();
            }

            if (!_disposed)
            {
                Update(job);
            }
        }

        Raise(job);

        // Only now, with the last Changed delivered, is the job let go: a listener that waited for
        // idle and then read its own state would otherwise still see it running.
        lock (_gate)
        {
            job.Worker = null;
        }

        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RunHooks(Job job, string path)
    {
        ExportJobOptions options = job.Options;
        try
        {
            if (options.OpenFolder)
            {
                _hooks.OpenFolder(path);
                lock (_gate)
                {
                    Note(job, "Shown in its folder.");
                }
            }

            if (options.RunScript is { } script)
            {
                lock (_gate)
                {
                    Note(job, $"Running {script}.");
                }

                _hooks.RunScript(script, path, line =>
                {
                    lock (_gate)
                    {
                        Note(job, line);
                    }
                }, _shutdown.Token);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The file is written; a hook that fails is worth saying, not worth failing the job for.
            _log.Warning(error, "A post-export step for {Job} failed", job.Id);
            lock (_gate)
            {
                Note(job, $"The step after the export failed: {error.Message}");
            }
        }
    }

    private Job? Find(string jobId) => _jobs.FirstOrDefault(candidate => string.Equals(candidate.Id, jobId, StringComparison.Ordinal));

    private IEnumerable<Job> Select(string? jobId) =>
        jobId is null ? [.. _jobs] : Find(jobId) is { } job ? [job] : [];

    /// <summary>Adds a line to a job's log, oldest dropped past the limit. Called under the gate.</summary>
    private void Note(Job job, string line)
    {
        job.Lines.Add($"{_clock.GetLocalNow():HH:mm:ss} {line}");
        if (job.Lines.Count > LogLimit)
        {
            job.Lines.RemoveAt(0);
        }
    }

    private void Load()
    {
        using SqliteCommand command = _database.CreateCommand();
        command.CommandText = "SELECT id, position, state, plan, project, project_path, created, finished, encoder, error, note, options, log, bytes FROM jobs ORDER BY position";
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            string id = reader.GetString(0);
            try
            {
                ExportPlan plan = JsonSerializer.Deserialize<ExportPlan>(reader.GetString(3), JazzJson.Options)!;
                string projectPath = reader.GetString(5);
                Project project = ProjectFile.Parse(reader.GetString(4), projectPath.Length > 0 ? projectPath : "queued.jazz").Project;
                ExportJobOptions options = reader.IsDBNull(11)
                    ? new ExportJobOptions()
                    : JsonSerializer.Deserialize<ExportJobOptions>(reader.GetString(11), JazzJson.Options) ?? new ExportJobOptions();

                var job = new Job(id, plan, project, projectPath, DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), options)
                {
                    Position = reader.GetInt64(1),
                    State = Enum.Parse<ExportJobState>(reader.GetString(2)),
                    Finished = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                    Encoder = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Error = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Note = reader.IsDBNull(10) ? null : reader.GetString(10),
                    Bytes = reader.IsDBNull(13) ? 0 : reader.GetInt64(13),
                };

                if (!reader.IsDBNull(12))
                {
                    job.Lines.AddRange(reader.GetString(12).Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }

                if (job.State == ExportJobState.Running)
                {
                    // It was running when the editor stopped. The partial file is gone; start again.
                    job.State = ExportJobState.Queued;
                    job.Note = Join(job.Note, "Restarted: Jazz Hands closed while it was exporting.");
                    Note(job, "Jazz Hands closed while it was exporting; queued again from the top.");
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

    private void AddColumn(string name, string type)
    {
        using SqliteCommand command = _database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            Execute($"ALTER TABLE jobs ADD COLUMN {name} {type}");
        }
    }

    private void Insert(Job job) => Execute(
        """
        INSERT INTO jobs (id, position, state, plan, project, project_path, created, finished, encoder, error, note, options, log, bytes)
        VALUES ($id, $position, $state, $plan, $project, $path, $created, NULL, NULL, NULL, NULL, $options, $log, 0)
        """,
        ("$id", job.Id),
        ("$position", job.Position),
        ("$state", job.State.ToString()),
        ("$plan", JsonSerializer.Serialize(job.Plan, JazzJson.Options)),
        ("$project", ProjectFile.Render(job.Project)),
        ("$path", job.ProjectPath),
        ("$created", job.Created.ToString("O", CultureInfo.InvariantCulture)),
        ("$options", JsonSerializer.Serialize(job.Options, JazzJson.Options)),
        ("$log", string.Join('\n', job.Lines)));

    private void Update(Job job) => Execute(
        "UPDATE jobs SET state = $state, finished = $finished, encoder = $encoder, error = $error, note = $note, options = $options, log = $log, bytes = $bytes WHERE id = $id",
        ("$id", job.Id),
        ("$state", job.State.ToString()),
        ("$finished", (object?)job.Finished?.ToString("O", CultureInfo.InvariantCulture)),
        ("$encoder", job.Encoder),
        ("$error", job.Error),
        ("$note", job.Note),
        ("$options", JsonSerializer.Serialize(job.Options, JazzJson.Options)),
        ("$log", string.Join('\n', job.Lines)),
        ("$bytes", job.Bytes));

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
    private sealed class Job(string id, ExportPlan plan, Project project, string projectPath, DateTimeOffset created, ExportJobOptions options)
    {
        public string Id { get; } = id;

        public ExportPlan Plan { get; } = plan;

        public Project Project { get; } = project;

        public string ProjectPath { get; } = projectPath;

        public DateTimeOffset Created { get; } = created;

        public ExportJobOptions Options { get; set; } = options;

        public long Position { get; set; }

        public ExportJobState State { get; set; } = ExportJobState.Queued;

        public ExportProgress Progress { get; set; }

        public DateTimeOffset? Finished { get; set; }

        public string? Encoder { get; set; }

        public string? Error { get; set; }

        public string? Note { get; set; }

        public long Bytes { get; set; }

        public bool Hardware { get; set; }

        public bool PauseRequested { get; set; }

        public Thread? Worker { get; set; }

        public List<string> Lines { get; } = [];

        public CancellationTokenSource Cancellation { get; set; } = new();

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
                State == ExportJobState.Done ? 1.0 : State == ExportJobState.Running ? Progress.Fraction : 0.0,
                Progress.Frame,
                Progress.TotalFrames,
                Progress.Fps,
                State == ExportJobState.Running ? eta : null,
                Bytes > 0 ? Bytes : Progress.Bytes,
                Encoder,
                Error,
                Created,
                Finished,
                Join(snaps, Note),
                Options.Priority,
                Hardware);
        }
    }
}

/// <summary>The post-export steps as a person's machine does them: Explorer, and the shell.</summary>
public sealed class ShellExportHooks : IExportHooks
{
    /// <inheritdoc />
    public void OpenFolder(string path)
    {
        // Explorer with the file selected; for an image sequence, the folder.
        string arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    /// <inheritdoc />
    public void RunScript(string script, string path, Action<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);

        // PowerShell and batch files need their interpreters; anything else runs as it is.
        string extension = Path.GetExtension(script).ToLowerInvariant();
        (string file, string arguments) = extension switch
        {
            ".ps1" => ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" \"{path}\""),
            ".cmd" or ".bat" => ("cmd.exe", $"/c \"\"{script}\" \"{path}\"\""),
            _ => (script, $"\"{path}\""),
        };

        var start = new System.Diagnostics.ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException($"'{script}' would not start.");
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is { Length: > 0 } text)
            {
                log(text);
            }
        };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { Length: > 0 } text)
            {
                log(text);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenRegistration stop = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        process.WaitForExit();
        log(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(script)} exited with {process.ExitCode}."));
    }
}
