using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using Serilog;

namespace JazzHands.Engine.Recovery;

/// <summary>
/// Keeps a recent copy of the project beside it, so a crash costs seconds rather than an evening.
/// </summary>
/// <remarks>
/// The copy goes to <c>&lt;project&gt;.jazz.d/recovery.jazz</c> and never to the project file
/// itself. Autosave must not touch what the user has saved: an editor that silently rewrites the
/// document while it is being edited takes away the one thing undo cannot give back, which is the
/// version on disk.
///
/// Two triggers, whichever comes first: sixty seconds of being dirty, or twenty five commands.
/// The command count matters because a fast editor can do a great deal of damage in under a
/// minute, and the timer matters because a slow one can leave a single large edit unsaved for
/// hours.
///
/// The clock is injected so the tests do not have to wait a minute to see it work.
/// </remarks>
public sealed class AutosaveService : IDisposable
{
    /// <summary>How long a dirty project waits before a copy is written.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    /// <summary>How many commands force a copy regardless of the clock.</summary>
    public const int DefaultCommandThreshold = 25;

    private readonly ILogger _log = Log.ForContext<AutosaveService>();
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly ITimer? _timer;

    private Func<Project>? _source;
    private int _commandsSinceSave;
    private bool _dirty;
    private bool _disposed;

    /// <summary>Creates the service for a project file.</summary>
    /// <param name="projectPath">The .jazz file this is protecting.</param>
    /// <param name="clock">The clock, injected for tests.</param>
    /// <param name="interval">How often a dirty project is copied.</param>
    /// <param name="commandThreshold">How many commands force a copy.</param>
    /// <param name="startTimer">False in tests that drive <see cref="Tick"/> by hand.</param>
    public AutosaveService(
        string projectPath,
        TimeProvider? clock = null,
        TimeSpan? interval = null,
        int commandThreshold = DefaultCommandThreshold,
        bool startTimer = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(commandThreshold, 1);

        ProjectPath = System.IO.Path.GetFullPath(projectPath);
        RecoveryPath = ProjectPaths.RecoveryFile(ProjectPath);
        CommandThreshold = commandThreshold;
        Interval = interval ?? DefaultInterval;

        _clock = clock ?? TimeProvider.System;

        if (startTimer)
        {
            _timer = _clock.CreateTimer(_ => Tick(), state: null, Interval, Interval);
        }
    }

    /// <summary>The project file being protected.</summary>
    public string ProjectPath { get; }

    /// <summary>Where the recent copy goes.</summary>
    public string RecoveryPath { get; }

    /// <summary>How often a dirty project is copied.</summary>
    public TimeSpan Interval { get; }

    /// <summary>How many commands force a copy.</summary>
    public int CommandThreshold { get; }

    /// <summary>How many copies have been written. For tests and the status bar.</summary>
    public int Saves { get; private set; }

    /// <summary>When the last copy was written, or null when none has been.</summary>
    public DateTimeOffset? LastSave { get; private set; }

    /// <summary>True when there are changes the recovery copy does not have.</summary>
    public bool IsDirty
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    /// <summary>Tells the service where to get the current project when it decides to write one.</summary>
    /// <remarks>
    /// A callback rather than a project, because the service outlives any one version of it and
    /// asking at the moment of writing is what keeps the copy current.
    /// </remarks>
    public void Watch(Func<Project> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            _source = source;
        }
    }

    /// <summary>Records that a command changed the project, writing a copy if enough have.</summary>
    public void OnCommand()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool write;
        lock (_gate)
        {
            _dirty = true;
            write = ++_commandsSinceSave >= CommandThreshold;
        }

        if (write)
        {
            WriteNow();
        }
    }

    /// <summary>What the timer does, exposed so tests can do it without waiting.</summary>
    public void Tick()
    {
        if (_disposed || !IsDirty)
        {
            return;
        }

        WriteNow();
    }

    /// <summary>
    /// Writes the recovery copy now, if there is something to write.
    /// </summary>
    /// <remarks>
    /// Failures are logged and swallowed. A full disk must not take down the editor, and the
    /// user finds out from the status bar rather than from a dialog in the middle of an edit.
    /// </remarks>
    public bool WriteNow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Func<Project>? source;
        lock (_gate)
        {
            source = _source;
        }

        if (source is null)
        {
            return false;
        }

        try
        {
            Project project = source();
            ProjectPaths.EnsureSidecar(ProjectPath);
            ProjectFile.WriteAtomic(RecoveryPath, ProjectFile.Render(ProjectFile.WithStoredPaths(ProjectPath, project)));

            lock (_gate)
            {
                _dirty = false;
                _commandsSinceSave = 0;
            }

            Saves++;
            LastSave = _clock.GetUtcNow();
            _log.Debug("Wrote recovery copy {Path}", RecoveryPath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warning(error, "Could not write the recovery copy {Path}", RecoveryPath);
            return false;
        }
    }

    /// <summary>
    /// Forgets the recovery copy, which is what saving the project does.
    /// </summary>
    /// <remarks>
    /// The copy has to go, not just be marked stale. A leftover recovery file offers to restore
    /// work the user has already saved, and after the second time that happens nobody trusts the
    /// prompt again.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _dirty = false;
            _commandsSinceSave = 0;
        }

        try
        {
            if (File.Exists(RecoveryPath))
            {
                File.Delete(RecoveryPath);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warning(error, "Could not remove the recovery copy {Path}", RecoveryPath);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
    }
}
