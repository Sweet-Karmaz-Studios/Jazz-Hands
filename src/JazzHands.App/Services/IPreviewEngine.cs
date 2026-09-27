using JazzHands.Core.Commands;
using JazzHands.Core.Time;
using JazzHands.Engine.Playback;
using JazzHands.Render;

namespace JazzHands.App.Services;

/// <summary>
/// What the preview panel needs from the playback engine besides commands.
/// </summary>
/// <remarks>
/// Commands go through <see cref="ISession"/> like everything else, so pressing play in the panel
/// is the same <c>playback.play</c> the CLI and MCP send. What is left is what a view needs to
/// draw: where frames come from, and when the playhead moves. An interface, so the panel can be
/// built and drawn in a test with no GPU and no sound card; there <see cref="Device"/> is null and
/// the picture area stays black.
/// </remarks>
public interface IPreviewEngine
{
    /// <summary>Raised on the composition thread when the frame on screen or the state changes.</summary>
    event EventHandler<PlayheadMovedEventArgs>? PlayheadMoved;

    /// <summary>The device frames are rendered on, or null when there is none.</summary>
    RenderDevice? Device { get; }

    /// <summary>Frames that were due at normal speed and never shown.</summary>
    long DroppedFrames { get; }

    /// <summary>Adds somewhere frames are shown.</summary>
    void AddTarget(IPreviewTarget target);

    /// <summary>Stops showing frames somewhere.</summary>
    void RemoveTarget(IPreviewTarget target);

    /// <summary>Presents the current frame again, after a resize or a zoom.</summary>
    void Refresh();

    /// <summary>What playback is doing now.</summary>
    PlaybackStateInfo Describe();

    /// <summary>
    /// Where the playhead is, cheaply: what the timeline reads every frame to draw it, where a
    /// full <see cref="Describe"/> would build a record sixty times a second.
    /// </summary>
    Flicks Position { get; }

    /// <summary>Raised on the composition thread with the scopes of the frame on screen while <see cref="Scopes"/> is on.</summary>
    event EventHandler<Render.Scopes.ScopeReading>? ScopesMeasured;

    /// <summary>Measure the scopes on each frame shown; the Scopes panel turns this on only while it is visible.</summary>
    bool Scopes { get; set; }

    /// <summary>The multicam clip shown as its grid of angles (Phase 41), or null for the program.</summary>
    string? MulticamGrid => null;
}

/// <summary>The playback engine, presented to the preview panel.</summary>
/// <param name="engine">The engine. Owned by the host, not by this adapter.</param>
/// <param name="device">The device it renders on.</param>
public sealed class EnginePreview(PlaybackEngine engine, RenderDevice device) : IPreviewEngine
{
    /// <inheritdoc />
    public event EventHandler<PlayheadMovedEventArgs>? PlayheadMoved
    {
        add => engine.PlayheadMoved += value;
        remove => engine.PlayheadMoved -= value;
    }

    /// <inheritdoc />
    public RenderDevice? Device => device;

    /// <inheritdoc />
    public long DroppedFrames => engine.DroppedFrames;

    /// <inheritdoc />
    public void AddTarget(IPreviewTarget target) => engine.AddTarget(target);

    /// <inheritdoc />
    public void RemoveTarget(IPreviewTarget target) => engine.RemoveTarget(target);

    /// <inheritdoc />
    public void Refresh() => engine.Refresh();

    /// <inheritdoc />
    public PlaybackStateInfo Describe() => engine.Describe();

    /// <inheritdoc />
    public Flicks Position => engine.Position;

    /// <inheritdoc />
    public event EventHandler<Render.Scopes.ScopeReading>? ScopesMeasured
    {
        add => engine.ScopesMeasured += value;
        remove => engine.ScopesMeasured -= value;
    }

    /// <inheritdoc />
    public bool Scopes
    {
        get => engine.Scopes;
        set => engine.Scopes = value;
    }

    /// <inheritdoc />
    public string? MulticamGrid => engine.MulticamGrid;
}

/// <summary>Opens and closes the full screen preview.</summary>
public interface IFullScreenPreview
{
    /// <summary>True while the full screen preview is open.</summary>
    bool IsOpen { get; }

    /// <summary>Raised when it opens or closes, including when it is closed from its own window.</summary>
    event EventHandler? IsOpenChanged;

    /// <summary>Opens it on the monitor the main window is not on, or closes it.</summary>
    void Toggle();
}
