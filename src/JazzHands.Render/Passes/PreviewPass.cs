using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using JazzHands.Render.Color;
using JazzHands.Render.Frames;
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
/// The two draws the single layer preview needs: a decoded frame onto a display target, and one
/// display target onto another at a size and position.
/// </summary>
/// <remarks>
/// Phase 10 replaces the first with the compositor, which works in linear light at half float and
/// stacks layers. Until then the preview shows the topmost clip, converted straight from YUV to
/// display-referred BT.709, which for SDR video is exactly what the compositor's output pass will
/// produce, because a decode followed by an encode through the same transfer function is the
/// identity. HDR is tone mapped with a simple curve until Phase 17 brings the real one.
///
/// The shader resource views over a frame's planes are made per draw and released straight
/// after, because a view holds a reference to its texture and a cache of them would keep textures
/// alive after the pool let them go. Views are cheap; Phase 10's render target pool owns this
/// properly.
///
/// Thread affine to the immediate context, like every pass.
/// </remarks>
public sealed class PreviewPass : IDisposable
{
    private readonly RenderDevice _device;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _framePixelShader;
    private readonly ID3D11PixelShader _blitPixelShader;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11ShaderResourceView?[] _views = new ID3D11ShaderResourceView?[3];
    private bool _disposed;

    /// <summary>Compiles the shaders and creates the pass state.</summary>
    public PreviewPass(RenderDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;

        string source = ReadShaderSource("Preview.hlsl");

        _vertexShader = device.Device.CreateVertexShader(Compile(source, "VsQuad", "vs_5_0"));
        _framePixelShader = device.Device.CreatePixelShader(Compile(source, "PsFrame", "ps_5_0"));
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

    /// <summary>Clears a target to opaque black.</summary>
    public void Clear(ID3D11RenderTargetView target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _device.ImmediateContext.ClearRenderTargetView(target, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
    }

    /// <summary>
    /// Draws a decoded frame onto part of a target, converting it from YUV to display RGB.
    /// </summary>
    /// <param name="frame">The frame, in any layout <see cref="PixelLayout"/> knows.</param>
    /// <param name="colorSpace">The frame's colour signalling.</param>
    /// <param name="target">The target, normally B8G8R8A8.</param>
    /// <param name="targetWidth">The target's width in pixels.</param>
    /// <param name="targetHeight">The target's height in pixels.</param>
    /// <param name="destination">Where on the target, as fractions of it.</param>
    public void DrawFrame(
        FrameTexture frame,
        YuvColorSpace colorSpace,
        ID3D11RenderTargetView target,
        int targetWidth,
        int targetHeight,
        QuadRect destination)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(colorSpace);
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Matrix4x4 matrix = colorSpace.Matrix;
        var constants = new QuadConstants
        {
            DestRect = ToVector(destination),
            SourceRect = ToVector(QuadRect.Full),
            MatrixRow0 = new Vector3(matrix.M11, matrix.M12, matrix.M13),
            SampleScale = SampleScaleFor(frame.Layout),
            MatrixRow1 = new Vector3(matrix.M21, matrix.M22, matrix.M23),
            LumaOffset = colorSpace.LumaOffset,
            MatrixRow2 = new Vector3(matrix.M31, matrix.M32, matrix.M33),
            ChromaOffset = 0.5f,
            LumaRange = colorSpace.LumaRange,
            ChromaRange = colorSpace.ChromaRange,
            TransferFunction = (uint)colorSpace.Transfer,
            IsPlanar = frame.Layout.IsSemiPlanar ? 0u : 1u,
        };

        try
        {
            for (int plane = 0; plane < frame.PlaneCount; plane++)
            {
                _views[plane] = _device.Device.CreateShaderResourceView(frame.Plane(plane));
            }

            // A semi-planar frame has no third plane; bind the second again so the slot is valid.
            _views[2] ??= _views[1];

            Draw(_framePixelShader, constants, target, targetWidth, targetHeight);
        }
        finally
        {
            ReleaseViews();
        }
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
            SampleScale = 1.0f,
        };

        try
        {
            _views[0] = _device.Device.CreateShaderResourceView(source);
            _views[1] = _views[0];
            _views[2] = _views[0];

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
        _framePixelShader.Dispose();
        _vertexShader.Dispose();
    }

    /// <summary>
    /// What turns a UNORM sample back into the fraction of full scale the matrix expects.
    /// </summary>
    /// <remarks>
    /// Three cases, and getting one wrong makes every pixel of that format nearly black or nearly
    /// white. Eight bit reads straight. P010 keeps ten bits at the top of a sixteen bit word, so a
    /// sample reads a hair short of what it means. Planar ten and twelve bit keep their bits at
    /// the bottom, so a sample reads 64 or 16 times too small.
    /// </remarks>
    internal static float SampleScaleFor(PixelLayout layout)
    {
        if (layout.BitDepth <= 8)
        {
            return 1.0f;
        }

        return layout.IsSemiPlanar
            ? 65535.0f / 65472.0f
            : 65535.0f / ((1 << layout.BitDepth) - 1);
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
        ID3D11ShaderResourceView? first = _views[0];
        ID3D11ShaderResourceView? second = _views[1];
        ID3D11ShaderResourceView? third = _views[2];

        first?.Dispose();

        if (!ReferenceEquals(second, first))
        {
            second?.Dispose();
        }

        if (!ReferenceEquals(third, first) && !ReferenceEquals(third, second))
        {
            third?.Dispose();
        }

        Array.Clear(_views);
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
        public Vector3 MatrixRow0;
        public float SampleScale;
        public Vector3 MatrixRow1;
        public float LumaOffset;
        public Vector3 MatrixRow2;
        public float ChromaOffset;
        public float LumaRange;
        public float ChromaRange;
        public uint TransferFunction;
        public uint IsPlanar;
    }
}
