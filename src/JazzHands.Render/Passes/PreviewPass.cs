using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using JazzHands.Render.Color;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace JazzHands.Render.Passes;

/// <summary>A rectangle as fractions of whatever it is placed on: 0 is the left or top edge, 1 the other.</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Right">The right edge.</param>
/// <param name="Bottom">The bottom edge.</param>
public readonly record struct QuadRect(float Left, float Top, float Right, float Bottom)
{
    /// <summary>The whole of the target or the whole of the source.</summary>
    public static QuadRect Full { get; } = new(0.0f, 0.0f, 1.0f, 1.0f);

    /// <summary>The width as a fraction.</summary>
    public float Width => Right - Left;

    /// <summary>The height as a fraction.</summary>
    public float Height => Bottom - Top;

    /// <summary>
    /// Where a picture of one shape goes on a target of another, as large as it fits without
    /// cropping, centred, with bars on the two sides that are left over.
    /// </summary>
    public static QuadRect Fit(int pictureWidth, int pictureHeight, int targetWidth, int targetHeight)
    {
        if (pictureWidth <= 0 || pictureHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return Full;
        }

        double picture = (double)pictureWidth / pictureHeight;
        double target = (double)targetWidth / targetHeight;

        if (picture > target)
        {
            float height = (float)(target / picture);
            return new QuadRect(0.0f, (1.0f - height) / 2.0f, 1.0f, (1.0f + height) / 2.0f);
        }

        float width = (float)(picture / target);
        return new QuadRect((1.0f - width) / 2.0f, 0.0f, (1.0f + width) / 2.0f, 1.0f);
    }

    /// <summary>
    /// Where a picture goes when it is shown at a fixed magnification rather than fitted: 1 is one
    /// picture pixel to one target pixel. Centred; the parts that fall off the target are simply
    /// off it, which the rasterizer clips.
    /// </summary>
    /// <param name="pictureWidth">The picture's width in pixels.</param>
    /// <param name="pictureHeight">The picture's height in pixels.</param>
    /// <param name="targetWidth">The target's width in pixels.</param>
    /// <param name="targetHeight">The target's height in pixels.</param>
    /// <param name="magnification">1 for one to one, 2 for twice the size.</param>
    /// <param name="panX">How far the picture is moved right, in target pixels.</param>
    /// <param name="panY">How far it is moved down, in target pixels.</param>
    public static QuadRect Zoom(
        int pictureWidth,
        int pictureHeight,
        int targetWidth,
        int targetHeight,
        double magnification,
        double panX = 0,
        double panY = 0)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            return Full;
        }

        double width = pictureWidth * magnification / targetWidth;
        double height = pictureHeight * magnification / targetHeight;
        double left = ((1.0 - width) / 2.0) + (panX / targetWidth);
        double top = ((1.0 - height) / 2.0) + (panY / targetHeight);

        return new QuadRect((float)left, (float)top, (float)(left + width), (float)(top + height));
    }
}

/// <summary>
/// Copies one display target onto another at a size and position: the program texture the
/// compositor wrote onto the preview surface, fitted, letterboxed or zoomed.
/// </summary>
/// <remarks>
/// The shader resource view over the source is made per draw and released straight after,
/// because a view holds a reference to its texture and a cache of them would keep a program
/// texture alive after a resize let it go.
///
/// Thread affine to the immediate context, like every pass.
/// </remarks>
public sealed class PreviewPass : IDisposable
{
    private readonly RenderDevice _device;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _blitPixelShader;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11ShaderResourceView?[] _views = new ID3D11ShaderResourceView?[1];
    private bool _disposed;

    /// <summary>Compiles the shaders and creates the pass state.</summary>
    public PreviewPass(RenderDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;

        string source = ReadShaderSource("Preview.hlsl");

        _vertexShader = device.Device.CreateVertexShader(Compile(source, "VsQuad", "vs_5_0"));
        _blitPixelShader = device.Device.CreatePixelShader(Compile(source, "PsBlit", "ps_5_0"));

        _constants = device.Device.CreateBuffer(
            new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<QuadConstants>(),
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

    /// <summary>What the screen the preview is shown on expects; sRGB unless the person says otherwise.</summary>
    public DisplayTransfer Display { get; set; } = DisplayTransfer.Srgb;

    /// <summary>Clears a target to opaque black.</summary>
    public void Clear(ID3D11RenderTargetView target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _device.ImmediateContext.ClearRenderTargetView(target, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
    }

    /// <summary>Copies part of one display target onto part of another, filtered.</summary>
    /// <param name="source">What to copy, normally the preview's program texture.</param>
    /// <param name="target">Where to copy it.</param>
    /// <param name="targetWidth">The target's width in pixels.</param>
    /// <param name="targetHeight">The target's height in pixels.</param>
    /// <param name="destination">Where on the target.</param>
    public void Blit(
        ID3D11Texture2D source,
        ID3D11RenderTargetView target,
        int targetWidth,
        int targetHeight,
        QuadRect destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var constants = new QuadConstants
        {
            DestRect = ToVector(destination),
            SourceRect = ToVector(QuadRect.Full),
            Display = (uint)Display,
        };

        try
        {
            _views[0] = _device.Device.CreateShaderResourceView(source);

            Draw(_blitPixelShader, constants, target, targetWidth, targetHeight);
        }
        finally
        {
            ReleaseViews();
        }
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
        _blitPixelShader.Dispose();
        _vertexShader.Dispose();
    }

    private static Vector4 ToVector(QuadRect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private void Draw(
        ID3D11PixelShader pixelShader,
        QuadConstants constants,
        ID3D11RenderTargetView target,
        int targetWidth,
        int targetHeight)
    {
        ID3D11DeviceContext context = _device.ImmediateContext;

        MappedSubresource mapped = context.Map(_constants, 0, MapMode.WriteDiscard);
        try
        {
            unsafe
            {
                *(QuadConstants*)mapped.DataPointer = constants;
            }
        }
        finally
        {
            context.Unmap(_constants, 0);
        }

        context.ClearState();
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.VSSetShader(_vertexShader);
        context.VSSetConstantBuffer(0, _constants);
        context.PSSetShader(pixelShader);
        context.PSSetShaderResources(0, _views!);
        context.PSSetSampler(0, _sampler);
        context.PSSetConstantBuffer(0, _constants);
        context.OMSetRenderTargets(target);
        context.RSSetViewport(new Viewport(0, 0, targetWidth, targetHeight, 0.0f, 1.0f));

        context.Draw(4, 0);

        context.ClearState();
    }

    private void ReleaseViews()
    {
        _views[0]?.Dispose();
        _views[0] = null;
    }

    private static byte[] Compile(string source, string entryPoint, string profile)
    {
        Blob? errors = null;
        try
        {
            Compiler.Compile(
                source,
                defines: [],
                include: null!,
                entryPoint,
                "Preview.hlsl",
                profile,
                ShaderFlags.OptimizationLevel3 | ShaderFlags.WarningsAreErrors,
                EffectFlags.None,
                out Blob? bytecode,
                out errors);

            if (bytecode is null)
            {
                throw new RenderDeviceException(
                    $"Compiling {entryPoint} failed: {errors?.AsString() ?? "no compiler output"}");
            }

            using (bytecode)
            {
                return bytecode.AsSpan().ToArray();
            }
        }
        finally
        {
            errors?.Dispose();
        }
    }

    private static string ReadShaderSource(string name)
    {
        Assembly assembly = typeof(PreviewPass).Assembly;
        string resource = $"JazzHands.Render.Shaders.{name}";

        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new RenderDeviceException(
                $"Shader '{resource}' is missing from the assembly. Is it marked as an EmbeddedResource?");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Matches the cbuffer in Preview.hlsl. Field order and padding are load bearing.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct QuadConstants
    {
        public Vector4 DestRect;
        public Vector4 SourceRect;
        public uint Display;
        public Vector3 Padding;
    }
}
