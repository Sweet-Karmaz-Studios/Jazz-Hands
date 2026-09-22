using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using JazzHands.Render.Color;
using Serilog;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace JazzHands.Render.Passes;

/// <summary>
/// Turns a decoder's YUV surface into linear-light RGBA, which is the format everything
/// downstream composites in.
/// </summary>
/// <remarks>
/// The input is normally a texture array slice that NVDEC decoded into, sampled in place: this
/// pass is where the zero-copy decode path pays off, because the frame goes from the decoder to
/// the compositor without ever touching system memory. NV12 and P010 surfaces expose their luma
/// and chroma planes through two shader resource views on the same resource, chosen by view
/// format; see the hw-decode skill.
/// </remarks>
public sealed class YuvToLinearPass : IDisposable
{
    private readonly ILogger _log = Log.ForContext<YuvToLinearPass>();
    private readonly RenderDevice _device;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private bool _disposed;

    /// <summary>Compiles the shaders and creates the pass state.</summary>
    public YuvToLinearPass(RenderDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;

        string source = ReadShaderSource("YuvToLinear.hlsl");

        using Blob vertexBlob = Compile(source, "VsMain", "vs_5_0");
        using Blob pixelBlob = Compile(source, "PsMain", "ps_5_0");

        _vertexShader = device.Device.CreateVertexShader(vertexBlob.AsSpan());
        _pixelShader = device.Device.CreatePixelShader(pixelBlob.AsSpan());

        _constants = device.Device.CreateBuffer(
            new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<YuvConstants>(),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            });

        _sampler = device.Device.CreateSamplerState(
            new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MaxLOD = float.MaxValue,
            });
    }

    /// <summary>
    /// Converts one slice of a decoder surface into <paramref name="target"/>.
    /// </summary>
    /// <param name="source">The decoder's NV12 or P010 texture, usually a texture array.</param>
    /// <param name="arraySlice">The slice holding this frame.</param>
    /// <param name="colorSpace">The source colour signalling.</param>
    /// <param name="target">A render target, normally R16G16B16A16_FLOAT.</param>
    public void Execute(ID3D11Texture2D source, int arraySlice, YuvColorSpace colorSpace, ID3D11Texture2D target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(colorSpace);
        ArgumentNullException.ThrowIfNull(target);

        Texture2DDescription sourceDescription = source.Description;
        Texture2DDescription targetDescription = target.Description;

        (Format lumaFormat, Format chromaFormat) = PlaneFormats(sourceDescription.Format);

        using ID3D11ShaderResourceView luma = CreatePlaneView(source, arraySlice, lumaFormat);
        using ID3D11ShaderResourceView chroma = CreatePlaneView(source, arraySlice, chromaFormat);
        using ID3D11RenderTargetView targetView = _device.Device.CreateRenderTargetView(target);

        UpdateConstants(colorSpace);

        ID3D11DeviceContext context = _device.ImmediateContext;
        context.ClearState();

        context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetShaderResources(0, [luma, chroma]);
        context.PSSetSampler(0, _sampler);
        context.PSSetConstantBuffer(0, _constants);
        context.OMSetRenderTargets(targetView);
        context.RSSetViewport(new Viewport(0, 0, targetDescription.Width, targetDescription.Height, 0.0f, 1.0f));

        // Three vertices, no buffers: the vertex shader builds a fullscreen triangle from the id.
        context.Draw(3, 0);

        context.ClearState();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sampler.Dispose();
        _constants.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    /// <summary>
    /// The view formats that expose a planar video surface's two planes. D3D11 selects the plane
    /// by the view format rather than by an index, which reads like a trick and is not.
    /// </summary>
    private static (Format Luma, Format Chroma) PlaneFormats(Format surfaceFormat) => surfaceFormat switch
    {
        Format.NV12 => (Format.R8_UNorm, Format.R8G8_UNorm),
        Format.P010 or Format.P016 => (Format.R16_UNorm, Format.R16G16_UNorm),
        _ => throw new NotSupportedException(
            $"{surfaceFormat} is not a planar video surface this pass can sample."),
    };

    private ID3D11ShaderResourceView CreatePlaneView(ID3D11Texture2D source, int arraySlice, Format format)
    {
        var description = new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2DArray,
            Texture2DArray = new Texture2DArrayShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1,
                FirstArraySlice = (uint)arraySlice,
                ArraySize = 1,
            },
        };

        return _device.Device.CreateShaderResourceView(source, description);
    }

    private void UpdateConstants(YuvColorSpace colorSpace)
    {
        Matrix4x4 matrix = colorSpace.Matrix;
        var constants = new YuvConstants
        {
            MatrixRow0 = new Vector3(matrix.M11, matrix.M12, matrix.M13),
            LumaScale = colorSpace.LumaScale,
            MatrixRow1 = new Vector3(matrix.M21, matrix.M22, matrix.M23),
            LumaOffset = colorSpace.LumaOffset,
            MatrixRow2 = new Vector3(matrix.M31, matrix.M32, matrix.M33),
            ChromaOffset = 0.5f,
            LumaRange = colorSpace.LumaRange,
            ChromaRange = colorSpace.ChromaRange,
            TransferFunction = (uint)colorSpace.Transfer,
            PeakLuminance = 100.0f,
        };

        ID3D11DeviceContext context = _device.ImmediateContext;
        MappedSubresource mapped = context.Map(_constants, 0, MapMode.WriteDiscard);
        try
        {
            unsafe
            {
                *(YuvConstants*)mapped.DataPointer = constants;
            }
        }
        finally
        {
            context.Unmap(_constants, 0);
        }
    }

    private static Blob Compile(string source, string entryPoint, string profile)
    {
        ShaderFlags flags = ShaderFlags.OptimizationLevel3 | ShaderFlags.WarningsAreErrors;

        Vortice.Direct3D.Blob? errors = null;
        try
        {
            Compiler.Compile(
                source,
                defines: [],
                include: null!,
                entryPoint,
                "YuvToLinear.hlsl",
                profile,
                flags,
                EffectFlags.None,
                out Vortice.Direct3D.Blob? bytecode,
                out errors);
            if (bytecode is null)
            {
                throw new RenderDeviceException(
                    $"Compiling {entryPoint} failed: {errors?.AsString() ?? "no compiler output"}");
            }

            return new Blob(bytecode);
        }
        finally
        {
            errors?.Dispose();
        }
    }

    private static string ReadShaderSource(string name)
    {
        Assembly assembly = typeof(YuvToLinearPass).Assembly;
        string resource = $"JazzHands.Render.Shaders.{name}";

        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new RenderDeviceException(
                $"Shader '{resource}' is missing from the assembly. Is it marked as an EmbeddedResource?");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Matches the cbuffer in YuvToLinear.hlsl. Field order and padding are load bearing.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct YuvConstants
    {
        public Vector3 MatrixRow0;
        public float LumaScale;
        public Vector3 MatrixRow1;
        public float LumaOffset;
        public Vector3 MatrixRow2;
        public float ChromaOffset;
        public float LumaRange;
        public float ChromaRange;
        public uint TransferFunction;
        public float PeakLuminance;
    }

    /// <summary>Owns a compiled shader blob.</summary>
    private sealed class Blob(Vortice.Direct3D.Blob inner) : IDisposable
    {
        public ReadOnlySpan<byte> AsSpan() => inner.AsSpan();

        public void Dispose() => inner.Dispose();
    }
}
