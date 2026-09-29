using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using JazzHands.Render;
using Serilog;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D9 = Vortice.Direct3D9;

namespace JazzHands.App.Controls.Preview;

/// <summary>
/// Presents a Direct3D 11 texture inside WPF with no CPU copy.
/// </summary>
/// <remarks>
/// WPF composes through Direct3D 9, and <see cref="D3DImage"/> takes a D3D9 surface as its back
/// buffer. A D3D11 texture created with the Shared misc flag can be opened by a D3D9Ex device
/// through its shared handle, so both APIs address the same allocation: the compositor renders
/// in D3D11 and WPF presents the result without the frame ever touching system memory. This is
/// the only zero-copy route in WPF. See the d3d-wpf-interop skill and Docs/SPIKES.md S1.
///
/// Threading: <see cref="Texture"/> is rendered on the caller's render thread.
/// <see cref="Present"/> must be called on the UI thread. The caller is responsible for not
/// drawing into the texture while a present is in flight; there is no keyed mutex, because D3D9
/// cannot open keyed-mutex handles.
/// </remarks>
public sealed class PreviewSurface : IDisposable
{
    private readonly ILogger _log = Log.ForContext<PreviewSurface>();
    private readonly RenderDevice _device;
    private readonly Dispatcher _dispatcher;

    private D9.IDirect3D9Ex? _d3d9;
    private D9.IDirect3DDevice9Ex? _device9;
    private D9.IDirect3DTexture9? _texture9;
    private D9.IDirect3DSurface9? _surface9;
    private ID3D11Texture2D? _texture11;
    private ID3D11Query? _flushQuery;
    private IntPtr _focusWindow;
    private bool _disposed;
    private bool _frontBufferLost;

    /// <summary>Creates a surface bound to a render device, with no back buffer until <see cref="Resize"/>.</summary>
    /// <param name="device">The device the compositor renders with.</param>
    public PreviewSurface(RenderDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Image = new D3DImage();
        Image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
    }

    /// <summary>Raised when the front buffer came back and the caller should redraw.</summary>
    public event EventHandler? SurfaceRecreated;

    /// <summary>The image source to put in an &lt;Image&gt;. Bind it once; it survives resizes.</summary>
    public D3DImage Image { get; }

    /// <summary>The texture the compositor renders into. Null until <see cref="Resize"/> has run.</summary>
    public ID3D11Texture2D? Texture => _texture11;

    /// <summary>The current back buffer width in pixels.</summary>
    public int PixelWidth { get; private set; }

    /// <summary>The current back buffer height in pixels.</summary>
    public int PixelHeight { get; private set; }

    /// <summary>True when WPF's front buffer is gone, for example during a GPU reset or a locked session.</summary>
    public bool IsFrontBufferLost => _frontBufferLost;

    /// <summary>How many times <see cref="Present"/> actually handed a frame to WPF.</summary>
    public long PresentedFrames { get; private set; }

    /// <summary>Milliseconds the last present spent acquiring the lock.</summary>
    public double LastLockMs { get; private set; }

    /// <summary>Milliseconds the last present spent in AddDirtyRect.</summary>
    public double LastDirtyMs { get; private set; }

    /// <summary>Milliseconds the last present spent in Unlock, which is where WPF takes the frame.</summary>
    public double LastUnlockMs { get; private set; }

    /// <summary>
    /// Makes the shared back buffer again on the render device as it now is, after the GPU was
    /// reset and the device made again (Phase 33). UI thread.
    /// </summary>
    public void Rebuild()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int width = Math.Max(1, PixelWidth);
        int height = Math.Max(1, PixelHeight);
        ReleaseBackBuffer();
        ReleaseD3D9Device();
        PixelWidth = 0;
        PixelHeight = 0;
        Resize(width, height);
        SurfaceRecreated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Allocates or reallocates the shared back buffer at the given pixel size. Cheap to call with
    /// an unchanged size, which is what makes it safe to call from a debounced SizeChanged handler.
    /// </summary>
    public void Resize(int pixelWidth, int pixelHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);

        if (pixelWidth == PixelWidth && pixelHeight == PixelHeight && _texture11 is not null)
        {
            return;
        }

        ReleaseBackBuffer();

        EnsureD3D9Device();

        // D3D11 side: BGRA to match D3DFMT_A8R8G8B8, shared so D3D9 can open it.
        var description = new Texture2DDescription
        {
            Width = (uint)pixelWidth,
            Height = (uint)pixelHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.Shared,
        };

        _texture11 = _device.Device.CreateTexture2D(description);

        using (IDXGIResource resource = _texture11.QueryInterface<IDXGIResource>())
        {
            IntPtr sharedHandle = resource.SharedHandle;

            // Passing the D3D11 handle in opens that allocation rather than creating a new one.
            _texture9 = _device9!.CreateTexture(
                (uint)pixelWidth,
                (uint)pixelHeight,
                1,
                D9.Usage.RenderTarget,
                D9.Format.A8R8G8B8,
                D9.Pool.Default,
                ref sharedHandle);
        }

        _surface9 = _texture9.GetSurfaceLevel(0);

        // An event query is how we know the D3D11 work is finished, since there is no mutex to
        // hand ownership across the API boundary.
        _flushQuery = _device.Device.CreateQuery(new QueryDescription { QueryType = QueryType.Event });

        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;

        AttachBackBuffer();

        _log.Debug("Preview surface resized to {Width}x{Height}", pixelWidth, pixelHeight);
    }

    /// <summary>
    /// Blocks until the GPU has finished the D3D11 work queued for the current frame. Call on the
    /// render thread after rendering and before handing the frame to <see cref="Present"/>.
    /// </summary>
    public void WaitForRenderCompletion()
    {
        if (_flushQuery is null)
        {
            return;
        }

        ID3D11DeviceContext context = _device.ImmediateContext;
        context.End(_flushQuery);
        context.Flush();

        // Spin rather than sleep: at 60 Hz the wait is a few hundred microseconds at most, and a
        // Sleep(1) here would cost a whole frame. But not for ever: a GPU that was reset or removed
        // never answers, and the spin held the presenter's lock while the UI thread waited on it,
        // which hung the editor (2026-09-29). A second is far past any real frame; the frame is
        // shown as it is and the device's own recovery takes it from there.
        var spin = new SpinWait();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!context.GetData(_flushQuery, out int done) || done == 0)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(1))
            {
                _log.Warning("The GPU did not finish the preview frame within a second ({Reason}); showing it as it is", _device.Device.DeviceRemovedReason);
                return;
            }

            spin.SpinOnce();
        }
    }

    /// <summary>
    /// Hands the current contents of <see cref="Texture"/> to WPF. UI thread only. Returns false
    /// when there is nothing to present, which is the normal state during a device reset or when
    /// WPF is still busy with the previous frame.
    /// </summary>
    /// <remarks>
    /// Two things here were settled by measurement in spike S1, and both are easy to get wrong.
    ///
    /// First, this must not be called from a CompositionTarget.Rendering handler. Locking inside
    /// that callback costs about 20 ms per frame and caps the preview at roughly 11 fps, because
    /// the lock is waiting on the very render pass whose callback is blocking. Called from a
    /// Dispatcher callback at Render priority instead, the same lock takes about 2 microseconds.
    ///
    /// Second, this uses the blocking <see cref="D3DImage.Lock"/> and not
    /// <see cref="D3DImage.TryLock"/>. TryLock with a finite timeout never acquires in this
    /// arrangement, at any timeout tried, so a non-blocking present presents nothing at all.
    /// </remarks>
    public bool Present()
    {
        _dispatcher.VerifyAccess();

        if (_disposed || _surface9 is null || _frontBufferLost || !Image.IsFrontBufferAvailable)
        {
            return false;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long stallThreshold = System.Diagnostics.Stopwatch.Frequency / 50;

        Image.Lock();

        long afterLock = System.Diagnostics.Stopwatch.GetTimestamp();
        LastLockMs = Elapsed(start);
        if (afterLock - start > stallThreshold)
        {
            _log.Warning("Present stalled for {Ms:F1} ms acquiring the D3DImage lock", LastLockMs);
        }

        try
        {
            Image.AddDirtyRect(new Int32Rect(0, 0, PixelWidth, PixelHeight));
            LastDirtyMs = Elapsed(afterLock);
        }
        finally
        {
            long beforeUnlock = System.Diagnostics.Stopwatch.GetTimestamp();
            Image.Unlock();
            LastUnlockMs = Elapsed(beforeUnlock);
        }

        PresentedFrames++;
        return true;

        static double Elapsed(long from) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Image.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;
        ReleaseBackBuffer();
        ReleaseD3D9Device();
    }

    private void EnsureD3D9Device()
    {
        if (_device9 is not null)
        {
            return;
        }

        _d3d9 = D9.D3D9.Direct3DCreate9Ex();
        _focusWindow = FocusWindow.Create();

        // The D3D9 device must sit on the same physical adapter as the D3D11 device, or the
        // "shared" surface is not shared at all: WPF copies it through system memory every frame,
        // which at 4K costs tens of milliseconds. Adapter 0 is not reliably the right one on a
        // machine with both an iGPU and a discrete card, so match by LUID.
        uint adapterIndex = FindMatchingAdapter(_d3d9, _device.AdapterLuid);

        var parameters = new D9.PresentParameters
        {
            Windowed = true,
            SwapEffect = D9.SwapEffect.Discard,
            DeviceWindowHandle = _focusWindow,
            PresentationInterval = D9.PresentInterval.Immediate,
            BackBufferFormat = D9.Format.Unknown,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
        };

        // FpuPreserve because WPF and the CLR both assume the x87 control word is left alone;
        // Multithreaded because the compositor renders off the UI thread.
        _device9 = _d3d9.CreateDeviceEx(
            adapterIndex,
            D9.DeviceType.Hardware,
            _focusWindow,
            D9.CreateFlags.HardwareVertexProcessing | D9.CreateFlags.Multithreaded | D9.CreateFlags.FpuPreserve,
            parameters);

        _log.Information(
            "D3D9Ex device created on adapter {Adapter} ({Description}) for the WPF preview bridge",
            adapterIndex,
            _d3d9.GetAdapterIdentifier(adapterIndex).Description);
    }

    private uint FindMatchingAdapter(D9.IDirect3D9Ex d3d9, long luid)
    {
        uint count = d3d9.AdapterCount;
        for (uint index = 0; index < count; index++)
        {
            D9.Luid candidate = d3d9.GetAdapterLuid(index);
            long packed = ((long)candidate.HighPart << 32) | (uint)candidate.LowPart;
            _log.Debug(
                "D3D9 adapter {Index}: {Description}, luid {Luid:X}",
                index,
                d3d9.GetAdapterIdentifier(index).Description,
                packed);

            if (packed == luid)
            {
                return index;
            }
        }

        _log.Warning(
            "No D3D9 adapter matches the render device luid {Luid:X}; falling back to adapter 0, which may cost a copy per frame",
            luid);
        return 0;
    }

    private void AttachBackBuffer()
    {
        if (_surface9 is null)
        {
            return;
        }

        Image.Lock();
        try
        {
            Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9.NativePointer, enableSoftwareFallback: false);
        }
        finally
        {
            Image.Unlock();
        }
    }

    private void OnFrontBufferAvailableChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        bool available = (bool)e.NewValue;
        _frontBufferLost = !available;

        if (!available)
        {
            _log.Warning("WPF front buffer lost; dropping the preview back buffer");
            Image.Lock();
            try
            {
                Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            }
            finally
            {
                Image.Unlock();
            }

            return;
        }

        _log.Information("WPF front buffer available again; rebuilding the preview bridge");

        int width = PixelWidth;
        int height = PixelHeight;
        ReleaseBackBuffer();
        ReleaseD3D9Device();

        if (width > 0 && height > 0)
        {
            Resize(width, height);
        }

        SurfaceRecreated?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseBackBuffer()
    {
        if (_surface9 is not null || _texture9 is not null)
        {
            Image.Lock();
            try
            {
                Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            }
            finally
            {
                Image.Unlock();
            }
        }

        _flushQuery?.Dispose();
        _flushQuery = null;
        _surface9?.Dispose();
        _surface9 = null;
        _texture9?.Dispose();
        _texture9 = null;
        _texture11?.Dispose();
        _texture11 = null;
        PixelWidth = 0;
        PixelHeight = 0;
    }

    private void ReleaseD3D9Device()
    {
        _device9?.Dispose();
        _device9 = null;
        _d3d9?.Dispose();
        _d3d9 = null;

        if (_focusWindow != IntPtr.Zero)
        {
            FocusWindow.Destroy(_focusWindow);
            _focusWindow = IntPtr.Zero;
        }
    }
}
