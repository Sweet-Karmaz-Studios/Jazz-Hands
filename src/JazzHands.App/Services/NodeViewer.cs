using System.Windows.Media;
using System.Windows.Media.Imaging;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Frames;
using Serilog;

namespace JazzHands.App.Services;

/// <summary>A comp node's picture for the Nodes panel's viewer (Phase 49a).</summary>
public interface INodeViewer
{
    /// <summary>
    /// The clip alone, its graph showing a node in place of its output, at a sequence time, small;
    /// null when it cannot be drawn. A newer request makes an older one that has not started give
    /// null at once.
    /// </summary>
    Task<ImageSource?> RenderAsync(Project project, string clipId, string nodeId, Flicks at, string projectPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Draws the viewer's pictures on WARP on a thread of its own (a <see cref="StillRenderer"/>), so
/// playback keeps the GPU and the UI thread only shows the frozen bitmap. Only the latest request
/// is drawn: picking nodes quickly does not queue a picture for each.
/// </summary>
public sealed class NodeViewer : INodeViewer, IDisposable
{
    /// <summary>How wide the viewer's pictures are drawn.</summary>
    public const int Width = 320;

    private readonly ILogger _log = Log.ForContext<NodeViewer>();
    private readonly StillRenderer _renderer = new();
    private readonly SemaphoreSlim _one = new(1, 1);
    private long _latest;

    /// <inheritdoc />
    public async Task<ImageSource?> RenderAsync(Project project, string clipId, string nodeId, Flicks at, string projectPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        long ticket = Interlocked.Increment(ref _latest);
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ticket != Interlocked.Read(ref _latest) || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            return await Task.Run(() => Draw(project, clipId, nodeId, at, projectPath), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _renderer.Dispose();
        _one.Dispose();
    }

    /// <summary>The clip's track alone in its sequence, so the viewer shows the node and not what is over or under it.</summary>
    internal static (Project Project, Sequence Sequence)? Solo(Project project, string clipId)
    {
        if (project.FindClip(clipId) is not { } found)
        {
            return null;
        }

        Sequence solo = found.Sequence with { Tracks = [found.Track with { Muted = false, Matte = null }] };
        return (project with { Sequences = [.. project.Sequences.Select(sequence => sequence.Id == solo.Id ? solo : sequence)] }, solo);
    }

    private ImageSource? Draw(Project project, string clipId, string nodeId, Flicks at, string projectPath)
    {
        try
        {
            if (Solo(project, clipId) is not { } alone)
            {
                return null;
            }

            (int width, int height, byte[] bgra) = _renderer.RenderPreview(alone.Project, alone.Sequence, at, Width, projectPath, nodeId);
            BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.Warning(error, "Node {Node} could not be drawn for the viewer", nodeId);
            return null;
        }
    }
}
