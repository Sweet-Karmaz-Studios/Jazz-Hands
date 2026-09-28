using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Playback;
using JazzHands.Render;
using Serilog;

namespace JazzHands.App.Services;

/// <summary>
/// Plays the source monitor's file (Phase 38): a second <see cref="PlaybackEngine"/> and its own
/// <see cref="Transport"/> over <see cref="SourceProject"/>, sharing the render device with the
/// program preview.
/// </summary>
/// <remarks>
/// Made on the first file opened, so an editor that never uses the source monitor never opens a
/// second sound stream or starts a second composition thread. It follows the session: a relinked
/// or replaced file plays its new self, and a removed one closes (<see cref="ItemGone"/>). The
/// transport is the program's format; when the project's sound format changes, it is made again.
/// Its own sound client means the two viewers can be heard together, as Windows mixes shared
/// streams.
/// </remarks>
public sealed class SourcePlayer : ISourcePlayer, ISourceScreen, IDisposable
{
    private readonly ILogger _log = Log.ForContext<SourcePlayer>();
    private readonly Session _session;
    private readonly Func<string?> _audioDevice;
    private readonly Func<PlaybackEngine> _makeEngine;
    private readonly List<IPreviewTarget> _targets = [];
    private readonly Lock _gate = new();
    private Transport? _transport;
    private PlaybackEngine? _engine;
    private MediaItem? _item;
    private bool _suspended;
    private ProjectSettings? _loaded;

    /// <summary>A player for a session.</summary>
    /// <param name="session">The session whose media it plays.</param>
    /// <param name="device">The render device the preview uses.</param>
    /// <param name="audioDevice">The sound card to play to, from the editor's settings.</param>
    /// <param name="options">How to play: hardware decode, the frame cache.</param>
    /// <param name="cache">The cache, for probes and keyframes.</param>
    public SourcePlayer(Session session, RenderDevice? device, Func<string?> audioDevice, PlaybackOptions? options = null, Media.Import.CacheManager? cache = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(audioDevice);
        _session = session;
        Device = device;
        _audioDevice = audioDevice;

        // A smaller frame cache than the program's: the source is one file, looked at a while.
        PlaybackOptions settings = (options ?? new PlaybackOptions()) with { FrameCacheBytes = 256L * 1024 * 1024 };
        _makeEngine = () => new PlaybackEngine(_transport!, Device!, settings, session.Notices, cache);
        _session.ProjectChanged += OnProjectChanged;
    }

    /// <summary>Raised when the playhead moves, on the composition thread.</summary>
    public event EventHandler<PlayheadMovedEventArgs>? PlayheadMoved;

    /// <summary>Raised when the open item leaves the project.</summary>
    public event EventHandler? ItemGone;

    /// <summary>The device frames are drawn on, or null on a machine with none.</summary>
    public RenderDevice? Device { get; }

    /// <inheritdoc />
    public Flicks Position => _engine?.Position ?? Flicks.Zero;

    /// <inheritdoc />
    public bool IsPlaying => _engine?.Describe().State == "playing";

    /// <summary>Frames the player could not show in time, for the dropped frame check.</summary>
    public long DroppedFrames => _engine?.DroppedFrames ?? 0;

    /// <summary>While true the player holds nothing, as the program preview does when the window is hidden.</summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            _suspended = value;
            _engine?.Suspended = value;
        }
    }

    /// <inheritdoc />
    public void Open(MediaItem? item, Flicks at)
    {
        lock (_gate)
        {
            _item = item;
            if (item is null)
            {
                _engine?.Pause();
                return;
            }

            if (Device is null)
            {
                return;
            }

            Load();
            _engine!.Seek(at);
        }
    }

    /// <inheritdoc />
    public void Seek(Flicks at) => _engine?.Seek(at);

    /// <inheritdoc />
    public void Play()
    {
        if (_item is not null)
        {
            _engine?.Play();
        }
    }

    /// <inheritdoc />
    public void Pause() => _engine?.Pause();

    /// <summary>Steps a number of frames, for the arrow keys.</summary>
    public void Step(int frames) => _engine?.Step(frames);

    /// <summary>Adds somewhere frames are shown.</summary>
    public void AddTarget(IPreviewTarget target)
    {
        lock (_gate)
        {
            _targets.Add(target);
            _engine?.AddTarget(target);
        }
    }

    /// <summary>Stops showing frames somewhere.</summary>
    public void RemoveTarget(IPreviewTarget target)
    {
        lock (_gate)
        {
            _targets.Remove(target);
            _engine?.RemoveTarget(target);
        }
    }

    /// <summary>Presents the current frame again.</summary>
    public void Refresh() => _engine?.Refresh();

    /// <summary>Loads the item's project into the transport and engine, making them when needed.</summary>
    private void Load()
    {
        _loaded = _session.Project.Settings;
        Project project = SourceProject.For(_session.Project, _item!);
        ProjectSettings format = project.SettingsFor(project.ActiveSequence!);
        if (_transport is null || _transport.Output.SampleRate != format.SampleRate || _transport.Output.Channels != format.ChannelCount)
        {
            Rebuild(format);
        }

        _transport!.Load(project, _session.ProjectPath);
        _engine!.Load(project, _session.ProjectPath);
    }

    private void Rebuild(ProjectSettings format)
    {
        Flicks position = Position;
        DisposeEngine();
        _transport = Transport.ForDefaultDevice(format.SampleRate, format.ChannelCount, _audioDevice());
        _engine = _makeEngine();
        _engine.Suspended = _suspended;
        _engine.PlayheadMoved += OnPlayheadMoved;
        foreach (IPreviewTarget target in _targets)
        {
            _engine.AddTarget(target);
        }

        _engine.Seek(position);
        _log.Information("Source monitor player made at {Rate} Hz, {Channels} channels", format.SampleRate, format.ChannelCount);
    }

    private void OnPlayheadMoved(object? sender, PlayheadMovedEventArgs e) => PlayheadMoved?.Invoke(this, e);

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        bool gone = false;
        lock (_gate)
        {
            if (_item is not { } item || _engine is null)
            {
                return;
            }

            MediaItem? now = _session.Project.MediaItem(item.Id);
            if (now is null)
            {
                _item = null;
                _engine.Pause();
                gone = true;
            }
            else if (now != item || _session.Project.Settings != _loaded)
            {
                // A relinked file or a new sound format; loading again is cheap, as the engine
                // keeps its decoders and cache for a file it already has.
                _item = now;
                Load();
            }
        }

        if (gone)
        {
            ItemGone?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DisposeEngine()
    {
        if (_engine is not null)
        {
            _engine.PlayheadMoved -= OnPlayheadMoved;
            _engine.Dispose();
            _engine = null;
        }

        _transport?.Dispose();
        _transport = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _session.ProjectChanged -= OnProjectChanged;
        lock (_gate)
        {
            DisposeEngine();
        }
    }
}

/// <summary>What the source panel's view draws into and hears from: the player, or a stand-in in tests.</summary>
public interface ISourceScreen
{
    /// <summary>Raised when the playhead moves, on any thread.</summary>
    event EventHandler<PlayheadMovedEventArgs>? PlayheadMoved;

    /// <summary>Raised when the open item leaves the project.</summary>
    event EventHandler? ItemGone;

    /// <summary>The device frames are drawn on, or null.</summary>
    RenderDevice? Device { get; }

    /// <summary>True to let go of everything while the window is hidden.</summary>
    bool Suspended { get; set; }

    /// <summary>Adds somewhere frames are shown.</summary>
    void AddTarget(IPreviewTarget target);

    /// <summary>Stops showing frames somewhere.</summary>
    void RemoveTarget(IPreviewTarget target);

    /// <summary>Frames the player could not show in time while playing.</summary>
    long DroppedFrames { get; }

    /// <summary>Presents the current frame again.</summary>
    void Refresh();
}
