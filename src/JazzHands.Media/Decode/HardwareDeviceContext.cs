using FFmpeg.AutoGen;
using JazzHands.Media.Interop;
using Serilog;

namespace JazzHands.Media.Decode;

/// <summary>
/// The Direct3D 11 device FFmpeg decodes into, wrapped as an FFmpeg hardware device context.
/// </summary>
/// <remarks>
/// The whole point is <see cref="CreateShared"/>: the decoder is handed the same
/// <c>ID3D11Device</c> the compositor renders with, so NVDEC writes into textures the compositor
/// can sample directly and a decoded frame never crosses the PCIe bus. The texture array is
/// created with <c>D3D11_BIND_SHADER_RESOURCE</c> for exactly that reason.
///
/// The shared device must have multithread protection enabled, because the decoder thread and the
/// render thread both use it. <c>RenderDevice.EnableMultithreadProtection</c> does that; this
/// class cannot, because Media does not depend on Render.
/// </remarks>
public sealed unsafe class HardwareDeviceContext : IDisposable
{
    private const uint D3D11BindShaderResource = 0x8;

    private readonly ILogger _log = Log.ForContext<HardwareDeviceContext>();
    private AvBufferRef? _buffer;

    private HardwareDeviceContext(AvBufferRef buffer, bool ownsDevice)
    {
        _buffer = buffer;
        OwnsDevice = ownsDevice;
    }

    /// <summary>True when FFmpeg created the Direct3D device rather than borrowing ours.</summary>
    public bool OwnsDevice { get; }

    /// <summary>True once disposed.</summary>
    public bool IsDisposed => _buffer is null || _buffer.IsDisposed;

    internal AVBufferRef* Handle => _buffer is null || _buffer.IsDisposed
        ? throw new ObjectDisposedException(nameof(HardwareDeviceContext))
        : _buffer.Handle;

    /// <summary>
    /// Wraps an existing Direct3D 11 device so the decoder and the compositor share it.
    /// </summary>
    /// <param name="device">An <c>ID3D11Device*</c>. Must have been created with video support.</param>
    /// <param name="immediateContext">
    /// An <c>ID3D11DeviceContext*</c> for the decoder. Pass the immediate context; FFmpeg
    /// serialises its own use of it.
    /// </param>
    public static HardwareDeviceContext CreateShared(IntPtr device, IntPtr immediateContext)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(device, IntPtr.Zero);
        ArgumentOutOfRangeException.ThrowIfEqual(immediateContext, IntPtr.Zero);

        FfmpegLoader.Initialize();

        AVBufferRef* raw = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (raw is null)
        {
            throw new FfmpegException("av_hwdevice_ctx_alloc returned null for D3D11VA.");
        }

        var buffer = new AvBufferRef(raw);
        try
        {
            var deviceContext = (AVHWDeviceContext*)raw->data;
            var d3d11 = (AVD3D11VADeviceContext*)deviceContext->hwctx;

            // FFmpeg releases both on teardown, so hand it references of its own.
            ComAddRef((void*)device);
            ComAddRef((void*)immediateContext);

            d3d11->device = (ID3D11Device*)device;
            d3d11->device_context = (ID3D11DeviceContext*)immediateContext;

            // Without this the decoder's texture array cannot be bound as a shader resource and
            // the compositor would have to copy every frame.
            d3d11->BindFlags = D3D11BindShaderResource;

            Av.Check(ffmpeg.av_hwdevice_ctx_init(raw), "av_hwdevice_ctx_init");

            Log.ForContext<HardwareDeviceContext>().Debug(
                "D3D11VA hardware context bound to the shared render device");

            return new HardwareDeviceContext(buffer, ownsDevice: false);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lets FFmpeg create its own Direct3D 11 device. Used by headless tools and tests that have
    /// no compositor; frames it produces must be downloaded before anything else can use them.
    /// </summary>
    public static HardwareDeviceContext CreateStandalone()
    {
        FfmpegLoader.Initialize();

        AVBufferRef* raw = null;
        int result = ffmpeg.av_hwdevice_ctx_create(
            &raw,
            AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
            null,
            null,
            0);

        if (result < 0 || raw is null)
        {
            throw new FfmpegException(result, "av_hwdevice_ctx_create", "D3D11VA");
        }

        return new HardwareDeviceContext(new AvBufferRef(raw), ownsDevice: true);
    }

    /// <summary>
    /// Tries to create a standalone context, returning null when the machine has no usable
    /// hardware decoder rather than throwing.
    /// </summary>
    public static HardwareDeviceContext? TryCreateStandalone()
    {
        try
        {
            return CreateStandalone();
        }
        catch (FfmpegException ex)
        {
            Log.ForContext<HardwareDeviceContext>().Information(
                "No D3D11VA hardware decoder available ({Reason}); decoding in software",
                ex.Message);
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }

    /// <summary>
    /// Calls IUnknown::AddRef through the COM vtable. Media deliberately has no Direct3D binding
    /// of its own, and this is the only COM call it needs.
    /// </summary>
    private static void ComAddRef(void* comObject)
    {
        void** vtable = *(void***)comObject;
        var addRef = (delegate* unmanaged[Stdcall]<void*, uint>)vtable[1];
        addRef(comObject);
    }
}
