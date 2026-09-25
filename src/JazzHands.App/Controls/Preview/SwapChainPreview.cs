using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using JazzHands.Render;
using Serilog;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JazzHands.App.Controls.Preview;

/// <summary>
/// Presents the compositor output through a DXGI flip-model swap chain hosted in a child window,
/// bypassing WPF's compositor entirely.
/// </summary>
/// <remarks>
/// This is the other half of spike S1. <see cref="PreviewSurface"/> hands frames to WPF, which
/// costs a recurring stall inside D3DImage.Lock; this path presents straight to the screen from
/// the render thread, so WPF never touches the frame.
///
/// The cost is airspace: a child HWND always draws above WPF content, so nothing WPF renders can
/// sit on top of the video. Overlays (safe margins, transform handles, the playhead readout) have
/// to be drawn into the D3D content instead. That is a real constraint on the preview panel
/// design, not an implementation detail.
/// </remarks>
public sealed partial class SwapChainPreview : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    private readonly ILogger _log = Log.ForContext<SwapChainPreview>();
    private readonly RenderDevice _device;
    private readonly Lock _gate = new();

    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private IntPtr _hwnd;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _tearingSupported;

    /// <summary>Creates the host for a render device.</summary>
    public SwapChainPreview(RenderDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>The back buffer the compositor renders into. Null before the window is built.</summary>
    public ID3D11Texture2D? BackBuffer => _backBuffer;

    /// <summary>The swap chain width in pixels.</summary>
    public int PixelWidth => _pixelWidth;

    /// <summary>The swap chain height in pixels.</summary>
    public int PixelHeight => _pixelHeight;

    /// <summary>Frames presented to the screen.</summary>
    public long PresentedFrames { get; private set; }

    /// <summary>Milliseconds the last Present call took.</summary>
    public double LastPresentMs { get; private set; }

    /// <summary>Wait for vertical blank when presenting. Off allows tearing where the driver permits it.</summary>
    public bool WaitForVBlank { get; set; } = true;

    /// <summary>
    /// Presents the current back buffer. Safe to call from the render thread: unlike
    /// <see cref="PreviewSurface"/>, nothing here needs the UI thread.
    /// </summary>
    public bool Present()
    {
        lock (_gate)
        {
            if (_swapChain is null)
            {
                return false;
            }

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            PresentFlags flags = WaitForVBlank || !_tearingSupported ? PresentFlags.None : PresentFlags.AllowTearing;
            _swapChain.Present(WaitForVBlank ? 1u : 0u, flags);
            LastPresentMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;

            PresentedFrames++;
            return true;
        }
    }

    /// <summary>
    /// Draws into the back buffer and presents it, holding the same lock a resize takes, so a
    /// resize on the UI thread can never swap the buffer out from under a draw on the render
    /// thread. Returns false before the window exists.
    /// </summary>
    /// <param name="draw">Draws into the back buffer, given its width and height in pixels.</param>
    public bool DrawAndPresent(Action<ID3D11Texture2D, int, int> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);

        lock (_gate)
        {
            if (_swapChain is null || _backBuffer is null)
            {
                return false;
            }

            draw(_backBuffer, _pixelWidth, _pixelHeight);
            return Present();
        }
    }

    /// <summary>Makes the swap chain again on the render device as it now is, after a GPU reset. UI thread.</summary>
    public void Rebuild()
    {
        lock (_gate)
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            _backBuffer?.Dispose();
            _backBuffer = null;
            _swapChain?.Dispose();
            _swapChain = null;
            CreateSwapChain();
        }
    }

    /// <summary>Resizes the swap chain buffers. Call when the host size changes.</summary>
    public void Resize(int pixelWidth, int pixelHeight)
    {
        lock (_gate)
        {
            if (_swapChain is null || (pixelWidth == _pixelWidth && pixelHeight == _pixelHeight))
            {
                return;
            }

            _backBuffer?.Dispose();
            _backBuffer = null;

            _swapChain.ResizeBuffers(
                2,
                (uint)Math.Max(1, pixelWidth),
                (uint)Math.Max(1, pixelHeight),
                Format.B8G8R8A8_UNorm,
                _tearingSupported ? SwapChainFlags.AllowTearing : SwapChainFlags.None);

            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _pixelWidth = pixelWidth;
            _pixelHeight = pixelHeight;
        }
    }

    /// <inheritdoc />
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(
            0,
            "STATIC",
            null,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not create the preview child window (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        CreateSwapChain();
        return new HandleRef(this, _hwnd);
    }

    /// <inheritdoc />
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        lock (_gate)
        {
            _backBuffer?.Dispose();
            _backBuffer = null;
            _swapChain?.Dispose();
            _swapChain = null;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        ArgumentNullException.ThrowIfNull(sizeInfo);
        base.OnRenderSizeChanged(sizeInfo);

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Resize(
            Math.Max(1, (int)(sizeInfo.NewSize.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)(sizeInfo.NewSize.Height * dpi.DpiScaleY)));
    }

    private void CreateSwapChain()
    {
        using IDXGIDevice dxgiDevice = _device.Device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        _tearingSupported = SupportsTearing(factory);

        var description = new SwapChainDescription1
        {
            Width = 1,
            Height = 1,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,

            // Flip model: the back buffer is handed straight to the desktop compositor with no
            // intermediate blit, which is the whole point of taking this route.
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = _tearingSupported ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
        };

        _swapChain = factory.CreateSwapChainForHwnd(_device.Device, _hwnd, description);

        // We handle resize ourselves; let DXGI leave Alt+Enter alone.
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);

        _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _pixelWidth = 1;
        _pixelHeight = 1;

        _log.Information(
            "Swap chain created on {Adapter}, flip-discard, tearing {Tearing}",
            _device.AdapterName,
            _tearingSupported ? "supported" : "unsupported");
    }

    private static bool SupportsTearing(IDXGIFactory2 factory)
    {
        try
        {
            using IDXGIFactory5 factory5 = factory.QueryInterface<IDXGIFactory5>();
            return factory5.PresentAllowTearing;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return false;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string? windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);
}
