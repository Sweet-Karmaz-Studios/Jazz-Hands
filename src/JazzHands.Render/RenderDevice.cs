using Serilog;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JazzHands.Render;

/// <summary>Which Direct3D 11 adapter a <see cref="RenderDevice"/> was created on.</summary>
public enum RenderDeviceKind
{
    /// <summary>A real GPU. What the editor and export use.</summary>
    Hardware,

    /// <summary>The software rasterizer. Used by tests, CI and machines with no usable GPU.</summary>
    Warp,
}

/// <summary>
/// The Direct3D 11 device the compositor draws with. One per process in the app; tests create
/// their own WARP device so render tests run on CI with no GPU.
/// </summary>
/// <remarks>
/// The decoder shares this device so hardware frames never leave the GPU. Device creation asks
/// for BGRA support because Direct2D (text and titles) requires it, and for video support so
/// D3D11VA can attach to the same device.
/// </remarks>
public sealed class RenderDevice : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
    ];

    private readonly ILogger _log = Log.ForContext<RenderDevice>();
    private bool _disposed;

    private RenderDevice(
        ID3D11Device device,
        ID3D11DeviceContext context,
        RenderDeviceKind kind,
        string adapterName,
        long adapterLuid,
        bool supportsVideo)
    {
        Device = device;
        ImmediateContext = context;
        Kind = kind;
        AdapterName = adapterName;
        AdapterLuid = adapterLuid;
        SupportsVideo = supportsVideo;
        FeatureLevel = device.FeatureLevel;
    }

    /// <summary>The underlying device.</summary>
    public ID3D11Device Device { get; }

    /// <summary>The immediate context. Only the render thread may touch it.</summary>
    public ID3D11DeviceContext ImmediateContext { get; }

    /// <summary>Whether this is a hardware or WARP device.</summary>
    public RenderDeviceKind Kind { get; }

    /// <summary>The adapter description, for the version banner and logs.</summary>
    public string AdapterName { get; }

    /// <summary>
    /// The adapter LUID. The preview bridge matches this against the D3D9 adapter list, because a
    /// shared surface that crosses adapters is copied through system memory on every frame.
    /// </summary>
    public long AdapterLuid { get; }

    /// <summary>The feature level the device was created at.</summary>
    public FeatureLevel FeatureLevel { get; }

    /// <summary>
    /// True when the device was created with video support, which D3D11VA needs to share it with
    /// the decoder. WARP has no video support, so headless tests decode in software.
    /// </summary>
    public bool SupportsVideo { get; }

    /// <summary>True for a real GPU.</summary>
    public bool IsHardware => Kind == RenderDeviceKind.Hardware;

    /// <summary>
    /// Creates a device on the best available adapter, falling back to WARP when no GPU can be
    /// used. Pass <paramref name="forceWarp"/> in tests that must be deterministic.
    /// </summary>
    public static RenderDevice Create(bool forceWarp = false, bool enableDebugLayer = false)
    {
        ILogger log = Log.ForContext<RenderDevice>();

        if (!forceWarp && TryCreate(DriverType.Unknown, enableDebugLayer, out RenderDevice? hardware))
        {
            log.Information(
                "Direct3D 11 device created on {Adapter} at {FeatureLevel}",
                hardware.AdapterName,
                hardware.FeatureLevel);
            return hardware;
        }

        if (TryCreate(DriverType.Warp, enableDebugLayer, out RenderDevice? warp))
        {
            if (!forceWarp)
            {
                log.Warning("No usable GPU found; falling back to the WARP software rasterizer");
            }

            return warp;
        }

        throw new RenderDeviceException(
            "Could not create a Direct3D 11 device, not even on WARP. The Windows graphics stack is unavailable.");
    }

    /// <summary>Creates a texture suitable for a render target and shader input.</summary>
    public ID3D11Texture2D CreateRenderTarget(int width, int height, Format format = Format.R16G16B16A16_Float)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        return Device.CreateTexture2D(description);
    }

    /// <summary>
    /// Enables Direct3D multithread protection. Required before the device is shared with the
    /// FFmpeg decoder, because the decode thread and the render thread both use it.
    /// </summary>
    public void EnableMultithreadProtection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using ID3D11Multithread multithread = Device.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
        _log.Debug("Direct3D multithread protection enabled for decoder sharing");
    }

    /// <summary>
    /// How much video memory this process has committed on the adapter, and what the driver is
    /// willing to give it. Used by the decoder pool budget and by the leak checks in spike S2.
    /// </summary>
    /// <returns>Bytes used and bytes budgeted, or zeroes when the adapter cannot report them.</returns>
    public (long UsedBytes, long BudgetBytes) QueryVideoMemory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            using IDXGIDevice dxgiDevice = Device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            using IDXGIAdapter3 adapter3 = adapter.QueryInterface<IDXGIAdapter3>();

            QueryVideoMemoryInfo info = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            return ((long)info.CurrentUsage, (long)info.Budget);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return (0, 0);
        }
    }

    /// <summary>Creates a CPU-readable staging texture matching an existing one, for readback.</summary>
    public ID3D11Texture2D CreateStagingTexture(ID3D11Texture2D source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        Texture2DDescription description = source.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;

        return Device.CreateTexture2D(description);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _log.Debug("Disposing the {Kind} render device", Kind);
        ImmediateContext.ClearState();
        ImmediateContext.Flush();
        ImmediateContext.Dispose();
        Device.Dispose();
    }

    private static bool TryCreate(DriverType driverType, bool enableDebugLayer, out RenderDevice device)
    {
        device = null!;

        DeviceCreationFlags debug = enableDebugLayer ? DeviceCreationFlags.Debug : DeviceCreationFlags.None;

        // Video support lets FFmpeg's d3d11va decoder share this device, so ask for it first.
        // WARP returns DXGI_ERROR_UNSUPPORTED for it, hence the second attempt.
        (DeviceCreationFlags Flags, bool Video)[] attempts =
        [
            (DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport | debug, true),
            (DeviceCreationFlags.BgraSupport | debug, false),
        ];

        IDXGIAdapter1? adapter = null;
        try
        {
            if (driverType == DriverType.Unknown)
            {
                adapter = FindBestAdapter();
                if (adapter is null)
                {
                    return false;
                }
            }

            foreach ((DeviceCreationFlags flags, bool video) in attempts)
            {
                SharpGen.Runtime.Result result = D3D11.D3D11CreateDevice(
                    adapter,
                    driverType,
                    flags,
                    FeatureLevels,
                    out ID3D11Device? created,
                    out ID3D11DeviceContext? context);

                if (result.Failure || created is null || context is null)
                {
                    created?.Dispose();
                    context?.Dispose();
                    Log.ForContext<RenderDevice>().Debug(
                        "D3D11CreateDevice({DriverType}, video: {Video}) returned {Result}",
                        driverType,
                        video,
                        result);
                    continue;
                }

                (string name, long luid) = adapter is not null
                    ? (adapter.Description1.Description, adapter.Description1.Luid)
                    : DescribeDevice(created);
                RenderDeviceKind kind = driverType == DriverType.Warp ? RenderDeviceKind.Warp : RenderDeviceKind.Hardware;
                device = new RenderDevice(created, context, kind, name, luid, video);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            Log.ForContext<RenderDevice>().Debug(ex, "Creating a {DriverType} device failed", driverType);
            return false;
        }
        finally
        {
            adapter?.Dispose();
        }
    }

    private static IDXGIAdapter1? FindBestAdapter()
    {
        using IDXGIFactory6 factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();

        // High-performance preference puts the discrete GPU first on a laptop or a hybrid desktop.
        for (uint index = 0; factory.EnumAdapterByGpuPreference(
                 index,
                 GpuPreference.HighPerformance,
                 out IDXGIAdapter1? adapter).Success; index++)
        {
            if (adapter is null)
            {
                continue;
            }

            if ((adapter.Description1.Flags & AdapterFlags.Software) == 0)
            {
                return adapter;
            }

            adapter.Dispose();
        }

        return null;
    }

    private static (string Name, long Luid) DescribeDevice(ID3D11Device device)
    {
        try
        {
            using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            return (adapter.Description.Description, adapter.Description.Luid);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return ("unknown adapter", 0);
        }
    }
}

/// <summary>Thrown when Direct3D cannot give us a usable device.</summary>
public sealed class RenderDeviceException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RenderDeviceException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public RenderDeviceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
