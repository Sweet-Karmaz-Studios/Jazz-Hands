using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using JazzHands.App.Services;
using JazzHands.Engine.Playback;
using JazzHands.Render;
using JazzHands.Render.Color;
using JazzHands.Render.Passes;
using Vortice.Direct3D11;

namespace JazzHands.App.Controls.Preview;

/// <summary>
/// The program on a whole monitor, for a client or a second screen.
/// </summary>
/// <remarks>
/// A borderless window maximized on the monitor the editor is not on, holding a DXGI flip model
/// swap chain rather than a D3DImage: spike S1 measured it at 60 fps with a third of the UI
/// thread cost, and it presents from the engine's thread without waiting for WPF at all. The
/// price is that WPF cannot draw over it, which is why the safe area guides stay in the panel.
///
/// Escape, F11 or a double click closes it. Every other key goes to the preview panel, so JKL and
/// the space bar keep working while the editor window is behind it.
/// </remarks>
internal sealed class FullScreenPreviewWindow : Window
{
    private readonly IPreviewEngine _engine;
    private readonly Func<Key, ModifierKeys, bool, bool> _keyDown;
    private readonly Func<Key, bool> _keyUp;
    private readonly SwapChainPreview _host;
    private SwapChainPresenter? _presenter;

    public FullScreenPreviewWindow(
        RenderDevice device,
        IPreviewEngine engine,
        Int32Rect monitor,
        Func<Key, ModifierKeys, bool, bool> keyDown,
        Func<Key, bool> keyUp,
        Func<DisplayTransfer>? display = null)
    {
        _engine = engine;
        _keyDown = keyDown;
        _keyUp = keyUp;
        _host = new SwapChainPreview(device);

        Title = "Jazz Hands preview";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Content = _host;
        Cursor = Cursors.None;

        // Placed on the target monitor first and maximized once shown, which is what puts it on
        // that monitor at that monitor's DPI rather than at the editor's.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.X;
        Top = monitor.Y;
        Width = Math.Max(1, monitor.Width / 2);
        Height = Math.Max(1, monitor.Height / 2);

        SourceInitialized += (_, _) =>
        {
            // WPF positions in device independent units; set the device pixel position on the
            // handle so a monitor at a different scale is still the one it lands on.
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, IntPtr.Zero, monitor.X, monitor.Y, monitor.Width, monitor.Height, 0x0004 | 0x0010);
            WindowState = WindowState.Maximized;
        };

        Loaded += (_, _) =>
        {
            _presenter = new SwapChainPresenter(device, _host, display);
            _engine.AddTarget(_presenter);
        };

        Closed += (_, _) =>
        {
            if (_presenter is not null)
            {
                _engine.RemoveTarget(_presenter);
                _presenter.Dispose();
                _presenter = null;
            }

            _host.Dispose();
        };
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape or Key.F11)
        {
            Close();
            e.Handled = true;
            return;
        }

        e.Handled = _keyDown(key, Keyboard.Modifiers, e.IsRepeat);
        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.Handled = _keyUp(e.Key);
        base.OnKeyUp(e);
    }

    /// <inheritdoc />
    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        Close();
        base.OnMouseDoubleClick(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

/// <summary>Draws engine frames into a swap chain, fitted, and presents them from the engine's thread.</summary>
internal sealed class SwapChainPresenter : IPreviewTarget, IDisposable
{
    private readonly RenderDevice _device;
    private readonly SwapChainPreview _host;
    private readonly PreviewPass _pass;
    private readonly Action<ID3D11Texture2D, int, int> _draw;
    private readonly Func<DisplayTransfer>? _display;
    private readonly Lock _sync = new();
    private PreviewFrame _frame;
    private bool _disposed;

    public SwapChainPresenter(RenderDevice device, SwapChainPreview host, Func<DisplayTransfer>? display = null)
    {
        _device = device;
        _display = display;
        _host = host;
        _pass = new PreviewPass(device);
        _draw = Draw;
    }

    /// <inheritdoc />
    public bool Present(in PreviewFrame frame)
    {
        // The engine may still be presenting to this target on its own thread for a moment after
        // it was removed, so disposing waits for any present in progress.
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            _frame = frame;
            return _host.DrawAndPresent(_draw);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pass.Dispose();
        }
    }

    /// <remarks>
    /// The view over the back buffer is made per draw and released at once: a swap chain cannot
    /// resize while anything still holds a reference to one of its buffers.
    /// </remarks>
    private void Draw(ID3D11Texture2D target, int width, int height)
    {
        using ID3D11RenderTargetView view = _device.Device.CreateRenderTargetView(target);
        _pass.Clear(view);
        _pass.Display = _display?.Invoke() ?? DisplayTransfer.Srgb;
        _pass.Blit(_frame.Texture, view, width, height, QuadRect.Fit(_frame.SequenceWidth, _frame.SequenceHeight, width, height));
    }
}
