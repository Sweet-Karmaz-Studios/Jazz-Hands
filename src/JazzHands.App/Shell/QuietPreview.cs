using JazzHands.App.ViewModels.Playback;
using JazzHands.Engine.Playback;

namespace JazzHands.App.Shell;

/// <summary>
/// The preview while the window is hidden: the engine lets go of its decoders, frame cache and
/// textures and stops ticking, and the panel lets go of its shared surfaces.
/// </summary>
/// <param name="engine">The playback engine, or null when there is no GPU.</param>
/// <param name="panel">The preview panel.</param>
public sealed class QuietPreview(PlaybackEngine? engine, PreviewPanelViewModel? panel) : IQuietWhileHidden
{
    /// <inheritdoc />
    public void SetQuiet(bool quiet)
    {
        // The panel first on the way down, so no surface is presented into while the engine goes;
        // the engine first on the way up, so the panel's first present has a frame.
        if (quiet)
        {
            panel?.IsSuspended = true;
            engine?.Suspended = true;
        }
        else
        {
            engine?.Suspended = false;
            panel?.IsSuspended = false;
        }
    }
}
