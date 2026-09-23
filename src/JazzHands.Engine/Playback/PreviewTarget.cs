using JazzHands.Core.Commands;
using JazzHands.Core.Time;
using Vortice.Direct3D11;

namespace JazzHands.Engine.Playback;

/// <summary>One rendered preview frame, as handed to the places that show it.</summary>
/// <param name="Texture">The program texture: B8G8R8A8, display referred, at the working resolution.</param>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="SequenceWidth">The sequence's own width, which Full quality renders at.</param>
/// <param name="SequenceHeight">The sequence's own height.</param>
/// <param name="Frame">The sequence frame it shows.</param>
/// <param name="Time">Where that frame starts on the timeline.</param>
/// <param name="Playhead">
/// Where the playhead was when the frame was chosen: the clock while playing. The difference
/// from <see cref="Time"/> is how far into its frame interval the picture went up.
/// </param>
/// <param name="Quality">The quality it was rendered at.</param>
public readonly record struct PreviewFrame(
    ID3D11Texture2D Texture,
    int Width,
    int Height,
    int SequenceWidth,
    int SequenceHeight,
    long Frame,
    Flicks Time,
    Flicks Playhead,
    PreviewQuality Quality);

/// <summary>
/// Somewhere a preview frame is shown: the preview panel, the full screen window, a test.
/// </summary>
/// <remarks>
/// Called on the composition thread, with the texture valid only for the duration of the call:
/// a target copies what it needs, at whatever size and position it shows it, and returns. That
/// keeps the engine free to render the next frame into the same texture, and means a target that
/// cannot take a frame right now (WPF still reading the last one) drops it rather than making
/// the engine wait.
/// </remarks>
public interface IPreviewTarget
{
    /// <summary>Takes a frame. Returns false when the frame was dropped rather than shown.</summary>
    bool Present(in PreviewFrame frame);
}

/// <summary>What the playhead is doing, raised when it moves or playback changes state.</summary>
/// <param name="Position">Where the playhead is.</param>
/// <param name="Frame">The sequence frame on screen.</param>
/// <param name="State">Playing, paused or stopped.</param>
/// <param name="Rate">1 for normal speed, negative for backwards.</param>
public sealed record PlayheadMovedEventArgs(Flicks Position, long Frame, TransportState State, double Rate);

/// <summary>How a <see cref="PlaybackEngine"/> is set up.</summary>
public sealed record PlaybackOptions
{
    /// <summary>Decode on the GPU where the device can. False forces the software path, as CI runs.</summary>
    public bool HardwareDecode { get; init; } = true;

    /// <summary>What decoded frames may hold in video memory.</summary>
    public long FrameCacheBytes { get; init; } = Frames.FrameCache.DefaultBudgetBytes;

    /// <summary>
    /// How many frames past the playhead to have decoded while playing. Filled in the time left
    /// over after each present, so a decode never lands on the frame that needed it.
    /// </summary>
    public int DecodeAhead { get; init; } = 8;

    /// <summary>How often <see cref="PlaybackEngine.PlayheadMoved"/> may fire while playing.</summary>
    public TimeSpan PlayheadEventInterval { get; init; } = TimeSpan.FromMilliseconds(33);

    /// <summary>How long after the last scrub seek Auto quality goes back to Full.</summary>
    public TimeSpan ScrubSettle { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Two seeks closer together than this, while not playing, are a scrub.</summary>
    public TimeSpan ScrubWindow { get; init; } = TimeSpan.FromMilliseconds(250);
}
