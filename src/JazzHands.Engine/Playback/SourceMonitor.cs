using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Engine.Playback;

/// <summary>What plays the source monitor's file, in an editor. Headless there is none, and the monitor is only marks.</summary>
public interface ISourcePlayer
{
    /// <summary>Where it is, in source time.</summary>
    Flicks Position { get; }

    /// <summary>True while it plays.</summary>
    bool IsPlaying { get; }

    /// <summary>Loads a file (or nothing) and puts the playhead at a source time.</summary>
    void Open(MediaItem? item, Flicks at);

    /// <summary>Moves the playhead.</summary>
    void Seek(Flicks at);

    /// <summary>Plays from the playhead.</summary>
    void Play();

    /// <summary>Stops where it is.</summary>
    void Pause();
}

/// <summary>
/// The source monitor's state (Phase 38): which media item it has open, its playhead, and its in
/// and out marks. View state: kept for the session rather than in the project, so it is not
/// undone or saved, but the <c>source.*</c> commands read and set it from any surface.
/// </summary>
/// <remarks>
/// Times are source times, on the file's frame grid. The out mark is exclusive, one frame past
/// the frame marked, as the sequence's is. An editor attaches an <see cref="ISourcePlayer"/>,
/// whose playhead is then the one that counts; without one the monitor keeps a playhead of its
/// own, so a headless session (<c>jazz serve</c>) can mark and edit the same way.
/// </remarks>
public sealed class SourceMonitor
{
    private readonly Lock _gate = new();
    private Flicks _position;

    /// <summary>Raised after anything here changes, on the thread that changed it.</summary>
    public event EventHandler? Changed;

    /// <summary>The player, in an editor.</summary>
    public ISourcePlayer? Player { get; set; }

    /// <summary>How many times something has been opened here, the same item again included: an open asks to be seen.</summary>
    public long Opens { get; private set; }

    /// <summary>The item open, or null.</summary>
    public string? MediaId { get; private set; }

    /// <summary>The in mark, or null.</summary>
    public Flicks? In { get; private set; }

    /// <summary>The out mark, exclusive, or null.</summary>
    public Flicks? Out { get; private set; }

    /// <summary>The source playhead.</summary>
    public Flicks Position => Player?.Position ?? _position;

    /// <summary>True while the player plays.</summary>
    public bool IsPlaying => Player?.IsPlaying ?? false;

    /// <summary>Opens an item at a source time; the marks are kept when it is the item already open.</summary>
    public void Open(MediaItem item, Flicks at)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (!string.Equals(MediaId, item.Id, StringComparison.Ordinal))
            {
                In = null;
                Out = null;
            }

            MediaId = item.Id;
            _position = at;
            Opens++;
        }

        Player?.Open(item, at);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the item, as when it leaves the project.</summary>
    public void Close()
    {
        lock (_gate)
        {
            MediaId = null;
            In = null;
            Out = null;
            _position = Flicks.Zero;
        }

        Player?.Open(null, Flicks.Zero);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the playhead.</summary>
    public void Seek(Flicks at)
    {
        lock (_gate)
        {
            _position = at;
        }

        Player?.Seek(at);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets both marks; the <c>source.set-in</c> and <c>source.set-out</c> handlers keep them in order.</summary>
    public void Mark(Flicks? markIn, Flicks? markOut)
    {
        lock (_gate)
        {
            In = markIn;
            Out = markOut;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Plays or pauses the player, when there is one.</summary>
    public void SetPlaying(bool play)
    {
        if (Player is not { } player)
        {
            return;
        }

        if (play)
        {
            player.Play();
        }
        else
        {
            player.Pause();
            lock (_gate)
            {
                _position = player.Position;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
