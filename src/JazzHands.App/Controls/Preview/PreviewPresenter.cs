using System.Windows.Threading;
using JazzHands.Engine.Playback;
using JazzHands.Render;
using JazzHands.Render.Color;
using JazzHands.Render.Passes;
using Serilog;
using Vortice.Direct3D11;

namespace JazzHands.App.Controls.Preview;

/// <summary>
/// Puts the engine's frames into a <see cref="PreviewSurface"/>, at the panel's size and zoom.
/// </summary>
/// <remarks>
/// Two threads meet here. The engine's composition thread calls <see cref="Present"/>: it draws
/// the program texture into the shared back buffer, waits for the GPU to finish, and posts the
/// hand-over to WPF to the UI thread at Render priority, which is the arrangement spike S1
/// measured. The UI thread resizes the back buffer when the panel changes size.
///
/// There is no keyed mutex, so the two are kept apart by hand. A lock covers the back buffer
/// while one side is drawing into it or replacing it, and a flag says a hand-over is still
/// waiting on the UI thread: while it is, the next frame is dropped rather than drawn into a
/// surface WPF may be reading. At 60 Hz that almost never happens; when the UI thread stalls it
/// is what keeps the picture from tearing, and the count of it is <see cref="Skipped"/>.
/// </remarks>
public sealed class PreviewPresenter : IPreviewTarget, IDisposable
{
    private readonly ILogger _log = Log.ForContext<PreviewPresenter>();
    private readonly RenderDevice _device;
    private readonly Dispatcher _dispatcher;
    private readonly PreviewSurface _surface;
    private readonly PreviewPass _pass;
    private readonly Lock _gate = new();
    private readonly Action _handOver;

    private ID3D11RenderTargetView? _view;
    private ID3D11Texture2D? _viewTexture;
    private int _pending;
    private bool _disposed;

    private double _magnification;
    private double _panX;
    private double _panY;
    private readonly Func<DisplayTransfer>? _display;

    /// <summary>Creates a presenter on the UI thread.</summary>
    /// <param name="device">The device frames are drawn on.</param>
    /// <param name="display">What the monitor expects, read at each present; sRGB when not given.</param>
    public PreviewPresenter(RenderDevice device, Func<DisplayTransfer>? display = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        _display = display;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _surface = new PreviewSurface(device);
        _pass = new PreviewPass(device);
        _handOver = HandOver;
    }

    /// <summary>The surface, whose image the view shows.</summary>
    public PreviewSurface Surface => _surface;

    /// <summary>Frames dropped because WPF had not yet taken the previous one.</summary>
    public long Skipped { get; private set; }

    /// <summary>
    /// How big the picture is drawn: zero to fit, otherwise screen pixels per sequence pixel.
    /// Panning moves it by whole screen pixels. Set from the UI thread; the next present uses it.
    /// </summary>
    public void SetZoom(double magnification, double panX, double panY)
    {
        lock (_gate)
        {
            _magnification = magnification;
            _panX = panX;
            _panY = panY;
        }
    }

    /// <summary>Makes the back buffer a new size. UI thread; cheap when the size has not changed.</summary>
    public void Resize(int pixelWidth, int pixelHeight)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ReleaseView();
            _surface.Resize(Math.Max(1, pixelWidth), Math.Max(1, pixelHeight));
        }
    }

    /// <inheritdoc />
    public bool Present(in PreviewFrame frame)
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
        {
            Skipped++;
            return false;
        }

        lock (_gate)
        {
            ID3D11Texture2D? target = _surface.Texture;
            if (_disposed || target is null)
            {
                Volatile.Write(ref _pending, 0);
                return false;
            }

            if (!ReferenceEquals(_viewTexture, target))
            {
                ReleaseView();
                _view = _device.Device.CreateRenderTargetView(target);
                _viewTexture = target;
            }

            int width = _surface.PixelWidth;
            int height = _surface.PixelHeight;

            QuadRect placement = _magnification <= 0
                ? QuadRect.Fit(frame.SequenceWidth, frame.SequenceHeight, width, height)
                : QuadRect.Zoom(frame.SequenceWidth, frame.SequenceHeight, width, height, _magnification, _panX, _panY);

            _pass.Display = _display?.Invoke() ?? DisplayTransfer.Srgb;
            _pass.Clear(_view!);
            _pass.Blit(frame.Texture, _view!, width, height, placement);
            _surface.WaitForRenderCompletion();
        }

        _dispatcher.InvokeAsync(_handOver, DispatcherPriority.Render);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseView();
            _pass.Dispose();
            _surface.Dispose();
        }
    }

    /// <summary>Gives WPF the frame. UI thread, at Render priority.</summary>
    private void HandOver()
    {
        try
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _surface.Present();
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log.Error(exception, "Handing the preview frame to WPF failed");
        }
        finally
        {
            Volatile.Write(ref _pending, 0);
        }
    }

    private void ReleaseView()
    {
        _view?.Dispose();
        _view = null;
        _viewTexture = null;
    }
}
