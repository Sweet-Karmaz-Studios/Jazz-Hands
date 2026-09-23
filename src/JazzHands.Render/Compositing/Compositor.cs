using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JazzHands.Core.Model;
using JazzHands.Render.Color;
using JazzHands.Render.Frames;
using JazzHands.Render.Shaders;
using Serilog;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace JazzHands.Render.Compositing;

/// <summary>How the finished stack is encoded for where it is going.</summary>
public enum OutputEncoding
{
    /// <summary>BT.1886, the display gamma SDR video is made for. The preview and SDR exports.</summary>
    Bt1886,

    /// <summary>sRGB, for PNG stills.</summary>
    Srgb,

    /// <summary>Left linear, for a float output.</summary>
    Linear,
}

/// <summary>How <see cref="Compositor.Output"/> writes a frame.</summary>
/// <param name="Encoding">The transfer function.</param>
/// <param name="DitherLevels">255 for an eight bit target, 1023 for ten bit, 0 for no dither.</param>
/// <param name="KeepAlpha">Write the stack's alpha instead of an opaque frame, for formats that carry it.</param>
/// <param name="Background">What shows through where nothing covers, premultiplied linear. Opaque black by default.</param>
public readonly record struct OutputSettings(
    OutputEncoding Encoding = OutputEncoding.Bt1886,
    int DitherLevels = 255,
    bool KeepAlpha = false,
    Vector4? Background = null)
{
    /// <summary>What the preview uses: BT.1886, dithered for eight bits, over black.</summary>
    public static OutputSettings Preview { get; } = new(OutputEncoding.Bt1886, 255);
}

/// <summary>
/// Draws a <see cref="RenderGraph"/>: every layer converted to linear light, placed, masked and
/// blended, bottom first, into a half float stack.
/// </summary>
/// <remarks>
/// The source pass turns each layer's decoded frame into premultiplied linear light at its own
/// size. A Normal layer with no masks is then drawn as a quad, cropped, scaled, rotated and
/// fitted by one matrix, straight onto the stack through premultiplied over blending, touching
/// only the pixels it covers. Any other layer is drawn into a frame-sized layer target first and
/// the composite pass blends it onto the stack with its opacity, blend mode and matte, writing a
/// new stack because a pass cannot read the target it writes; so is a layer the cache keeps. An
/// opaque untransformed frame-sized layer over nothing is the stack, with no drawing at all. An
/// adjustment layer runs its effects over the stack and composites the result back over it.
/// <see cref="Output"/> then encodes the stack for the preview or a file.
///
/// Everything intermediate comes from <see cref="Pool"/> and goes back to it within the frame, so
/// after the first frame at a size nothing is created. Transformed layers can be kept in
/// <see cref="Cache"/> between frames when the graph asks, which is what makes scrubbing over a
/// still redraw nothing.
///
/// Thread affine to the device's immediate context.
/// </remarks>
public sealed class Compositor : IDisposable
{
    private readonly ILogger _log = Log.ForContext<Compositor>();
    private readonly RenderDevice _device;
    private readonly bool _ownsPool;
    private readonly ID3D11SamplerState[] _samplers;
    private readonly ID3D11BlendState _over;
    private readonly ID3D11ShaderResourceView?[] _views = new ID3D11ShaderResourceView?[4];
    private readonly ID3D11ShaderResourceView?[] _planeViews = new ID3D11ShaderResourceView?[4];
    private readonly Dictionary<Type, ID3D11Buffer> _constants = [];
    private readonly MaskRasterizer _masks;
    private readonly EffectContextFactory _effectContexts;

    private Shaders? _shaders;
    private int _shaderGeneration = -1;
    private bool _disposed;

    /// <summary>Creates a compositor on a device.</summary>
    /// <param name="device">The device.</param>
    /// <param name="pool">A pool to share with other renderers, or null for its own.</param>
    /// <param name="cacheCapacity">How many transformed layers the cache may hold.</param>
    public Compositor(RenderDevice device, RenderTargetPool? pool = null, int cacheCapacity = 8)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        _ownsPool = pool is null;
        Pool = pool ?? new RenderTargetPool(device);
        Cache = new LayerCache(Pool, cacheCapacity);
        _masks = new MaskRasterizer(device);
        _effectContexts = new EffectContextFactory(device, Pool);

        _samplers =
        [
            Sampler(Filter.MinMagMipLinear, TextureAddressMode.Clamp),
            Sampler(Filter.MinMagMipPoint, TextureAddressMode.Clamp),
            Sampler(Filter.MinMagMipLinear, TextureAddressMode.Wrap),
        ];

        // Premultiplied over, colour and alpha alike: the Normal composite, done by the output merger.
        _over = device.Device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
    }

    /// <summary>Where every intermediate target comes from.</summary>
    public RenderTargetPool Pool { get; }

    /// <summary>Transformed layers kept between frames.</summary>
    public LayerCache Cache { get; }

    /// <summary>Layers drawn, for diagnostics.</summary>
    public long LayersDrawn { get; private set; }

    /// <summary>
    /// Draws a graph into a stack of premultiplied linear light at the graph's size.
    /// </summary>
    /// <returns>A target rented from <see cref="Pool"/>; the caller returns it.</returns>
    public RenderTarget Render(RenderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureShaders();

        // No stack until something needs one underneath it: the bottom layer of most frames is
        // opaque and untransformed, and then it simply is the stack.
        RenderTarget? stack = null;

        foreach (LayerNode layer in graph.Layers)
        {
            if (layer.Opacity <= 0.0f)
            {
                continue;
            }

            stack = layer.IsAdjustment ? Adjust(graph, layer, stack ?? Empty(graph)) : Place(graph, layer, stack);
            LayersDrawn++;
        }

        return stack ?? Empty(graph);
    }

    /// <summary>
    /// Encodes a stack for display or a file, into a target of the same size.
    /// </summary>
    public void Output(RenderTarget stack, ID3D11RenderTargetView target, int width, int height, OutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureShaders();

        var constants = new OutputConstants
        {
            Background = settings.Background ?? new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            Encoding = (uint)settings.Encoding,
            DitherLevels = (uint)Math.Max(0, settings.DitherLevels),
            KeepAlpha = settings.KeepAlpha ? 1u : 0u,
        };

        _views[0] = stack.Resource;
        FullScreen(_shaders!.OutputPixel, target, width, height, in constants, 1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cache.Dispose();
        _masks.Dispose();
        _shaders?.Dispose();

        foreach (ID3D11Buffer buffer in _constants.Values)
        {
            buffer.Dispose();
        }

        foreach (ID3D11SamplerState sampler in _samplers)
        {
            sampler.Dispose();
        }

        _over.Dispose();

        if (_ownsPool)
        {
            Pool.Dispose();
        }
    }

    /// <summary>
    /// What turns a UNORM sample back into the fraction of full scale the matrix expects: P010
    /// keeps ten bits at the top of sixteen, planar ten and twelve bit keep them at the bottom.
    /// </summary>
    internal static float SampleScaleFor(PixelLayout layout)
    {
        if (layout.BitDepth <= 8 || layout.IsRgb)
        {
            return 1.0f;
        }

        return layout.IsSemiPlanar
            ? 65535.0f / 65472.0f
            : 65535.0f / ((1 << layout.BitDepth) - 1);
    }

    /// <summary>A transparent frame-sized stack.</summary>
    private RenderTarget Empty(RenderGraph graph)
    {
        RenderTarget stack = Pool.Rent(graph.Width, graph.Height);
        Clear(stack, Vector4.Zero);
        return stack;
    }

    /// <summary>
    /// Draws a picture layer onto the stack and returns the new stack. A null stack is
    /// transparent black.
    /// </summary>
    private RenderTarget Place(RenderGraph graph, LayerNode layer, RenderTarget? stack)
    {
        LayerKey? key = LayerKeyFor(graph, layer);

        // Normal with no masks and nothing to keep between frames: the quad goes straight onto
        // the stack through premultiplied over. Only the pixels under it are touched, where a
        // placed target and the composite pass would each clear, read or write the whole frame;
        // four quarter-frame layers at 4K cost a fifth as much. The one exception is an opaque
        // untransformed layer over nothing, which below becomes the stack without any drawing.
        bool fillsFrame = stack is null && layer.Opacity >= 1.0f && IsInPlace(graph, layer);
        if (key is null && layer.Masks.IsDefaultOrEmpty && layer.Blend == BlendMode.Normal && !fillsFrame)
        {
            stack ??= Empty(graph);
            RenderTarget source = Linear(layer);
            DrawQuad(graph, layer, source, stack.View, Math.Clamp(layer.Opacity, 0.0f, 1.0f), _over);
            Pool.Return(source);
            return stack;
        }

        RenderTarget? placed = null;
        bool cached = key is { } lookup && Cache.TryGet(lookup, out placed);
        placed ??= Transform(graph, layer);

        // Normal at full opacity over nothing is the layer itself, so there is nothing to blend.
        // A layer the cache keeps is copied rather than handed over, because the stack is
        // returned to the pool at the end of the frame and the cache still holds it.
        if (stack is null && layer.Masks.IsDefaultOrEmpty && layer.Blend == BlendMode.Normal && layer.Opacity >= 1.0f)
        {
            if (cached || key is not null)
            {
                RenderTarget copy = Pool.Rent(graph.Width, graph.Height);
                _device.ImmediateContext.CopyResource(copy.Texture, placed.Texture);

                if (!cached)
                {
                    Cache.Put(key!.Value, placed);
                }

                return copy;
            }

            return placed;
        }

        stack ??= Empty(graph);
        RenderTarget? matte = layer.Masks.IsDefaultOrEmpty ? null : Matte(graph, layer);
        RenderTarget result = Composite(stack, placed, matte, layer.Opacity, layer.Blend);

        if (matte is not null)
        {
            Pool.Return(matte);
        }

        if (!cached)
        {
            if (key is { } store)
            {
                Cache.Put(store, placed);
            }
            else
            {
                Pool.Return(placed);
            }
        }

        Pool.Return(stack);
        return result;
    }

    /// <summary>Runs an adjustment layer's effects over the stack and composites the result back.</summary>
    private RenderTarget Adjust(RenderGraph graph, LayerNode layer, RenderTarget stack)
    {
        if (layer.Effects.IsDefaultOrEmpty)
        {
            return stack;
        }

        EffectContext context = _effectContexts.For(layer.Scale);
        RenderTarget input = stack;

        foreach (ILayerEffect effect in layer.Effects)
        {
            RenderTarget output = Pool.Rent(graph.Width, graph.Height);
            effect.Apply(context, input, output);

            if (!ReferenceEquals(input, stack))
            {
                Pool.Return(input);
            }

            input = output;
        }

        RenderTarget? matte = layer.Masks.IsDefaultOrEmpty ? null : Matte(graph, layer);
        RenderTarget result = Composite(stack, input, matte, layer.Opacity, layer.Blend);

        if (matte is not null)
        {
            Pool.Return(matte);
        }

        Pool.Return(input);
        Pool.Return(stack);
        return result;
    }

    /// <summary>The source in linear light, placed in a frame-sized transparent target.</summary>
    private RenderTarget Transform(RenderGraph graph, LayerNode layer)
    {
        RenderTarget source = Linear(layer);

        // A frame-sized source with nothing moved or cropped maps every texel onto itself, and
        // either filter sampled at texel centres gives the texel back: the pass would be a copy.
        if (IsInPlace(graph, layer) && source.Width == graph.Width && source.Height == graph.Height)
        {
            return source;
        }

        RenderTarget placed = Pool.Rent(graph.Width, graph.Height);
        Clear(placed, Vector4.Zero);
        DrawQuad(graph, layer, source, placed.View, 1.0f, blend: null);

        Pool.Return(source);
        return placed;
    }

    /// <summary>True when a layer's picture is the frame's size and nothing moves or crops it.</summary>
    private static bool IsInPlace(RenderGraph graph, LayerNode layer) =>
        layer.Transform.IsIdentity
        && layer.Crop == LayerNode.NoCrop
        && layer.SourceWidth == graph.Width
        && layer.SourceHeight == graph.Height;

    /// <summary>
    /// Draws a layer's linear source through its matrix onto a frame-sized target: replacing what
    /// is there, or blended over it when <paramref name="blend"/> is given.
    /// </summary>
    private void DrawQuad(RenderGraph graph, LayerNode layer, RenderTarget source, ID3D11RenderTargetView target, float opacity, ID3D11BlendState? blend)
    {
        Matrix3x2 m = layer.Transform;
        var constants = new TransformConstants
        {
            MatrixRow0 = new Vector4(m.M11, m.M21, m.M31, 0.0f),
            MatrixRow1 = new Vector4(m.M12, m.M22, m.M32, 0.0f),
            TargetSize = new Vector2(graph.Width, graph.Height),
            SourceSize = new Vector2(layer.SourceWidth, layer.SourceHeight),
            Crop = layer.Crop,
            TextureSize = new Vector2(source.Width, source.Height),
            Bicubic = graph.Bicubic && source.Width > 1 && source.Height > 1 ? 1u : 0u,
            Opacity = opacity,
        };

        _views[0] = source.Resource;
        Draw(_shaders!.TransformVertex, _shaders.TransformPixel, target, graph.Width, graph.Height, in constants, 1, PrimitiveTopology.TriangleStrip, 4, blend);
    }

    /// <summary>A layer's picture as premultiplied linear light at its own size.</summary>
    private RenderTarget Linear(LayerNode layer)
    {
        switch (layer.Source)
        {
            case FrameLayerSource frame:
                return Convert(frame.Frame, frame.Color);

            case SolidLayerSource solid:
                RenderTarget texel = Pool.Rent(1, 1);
                Clear(texel, solid.Color);
                return texel;

            case NestedLayerSource nested:
                return Render(nested.Graph);

            default:
                throw new NotSupportedException($"{layer.Source.GetType().Name} is not a source the compositor knows.");
        }
    }

    /// <summary>The source pass: any decoded layout to premultiplied linear light.</summary>
    private RenderTarget Convert(FrameTexture frame, YuvColorSpace color)
    {
        RenderTarget linear = Pool.Rent(frame.Width, frame.Height);
        PixelLayout layout = frame.Layout;

        Matrix4x4 matrix = color.Matrix;
        SampleRange range = color.RangeFor(layout.BitDepth);
        var constants = new SourceConstants
        {
            MatrixRow0 = new Vector3(matrix.M11, matrix.M12, matrix.M13),
            SampleScale = SampleScaleFor(layout),
            MatrixRow1 = new Vector3(matrix.M21, matrix.M22, matrix.M23),
            LumaOffset = range.LumaOffset,
            MatrixRow2 = new Vector3(matrix.M31, matrix.M32, matrix.M33),
            ChromaOffset = range.ChromaOffset,
            LumaRange = range.LumaRange,
            ChromaRange = range.ChromaRange,
            Transfer = (uint)color.Transfer,
            Layout = layout.IsRgb
                ? (layout.PlaneCount == 1 ? 2u : 3u)
                : layout.IsSemiPlanar ? 0u : 1u,
        };

        try
        {
            for (int plane = 0; plane < frame.PlaneCount; plane++)
            {
                _planeViews[plane] = frame.CreateView(_device.Device, plane);
            }

            for (int plane = 0; plane < 4; plane++)
            {
                _views[plane] = _planeViews[Math.Min(plane, frame.PlaneCount - 1)];
            }

            FullScreen(_shaders!.SourcePixel, linear.View, linear.Width, linear.Height, in constants, 4);
        }
        finally
        {
            for (int plane = 0; plane < _planeViews.Length; plane++)
            {
                _planeViews[plane]?.Dispose();
                _planeViews[plane] = null;
            }
        }

        return linear;
    }

    /// <summary>Blends a placed layer onto the stack, into a new target.</summary>
    private RenderTarget Composite(RenderTarget stack, RenderTarget layer, RenderTarget? matte, float opacity, BlendMode blend)
    {
        RenderTarget result = Pool.Rent(stack.Width, stack.Height);

        var constants = new CompositeConstants
        {
            Opacity = Math.Clamp(opacity, 0.0f, 1.0f),
            Mode = (uint)blend,
            HasMatte = matte is null ? 0u : 1u,
        };

        _views[0] = stack.Resource;
        _views[1] = layer.Resource;
        _views[2] = matte?.Resource ?? layer.Resource;
        FullScreen(_shaders!.CompositePixel, result.View, result.Width, result.Height, in constants, 3);

        return result;
    }

    /// <summary>
    /// Every mask on a layer, rasterized in frame space, feathered and combined into one matte.
    /// </summary>
    private RenderTarget Matte(RenderGraph graph, LayerNode layer)
    {
        RenderTarget? matte = null;
        bool first = true;

        foreach (MatteShape shape in layer.Masks)
        {
            RenderTarget coverage = Pool.Rent(graph.Width, graph.Height, Format.B8G8R8A8_UNorm);
            _masks.Rasterize(coverage, shape, layer.Transform);

            float sigma = Math.Clamp(shape.Feather * layer.Scale / 2.0f, 0.0f, 64.0f);
            RenderTarget feathered = sigma > 0.25f ? Feather(coverage, sigma) : coverage;
            if (!ReferenceEquals(feathered, coverage))
            {
                Pool.Return(coverage);
            }

            RenderTarget combined = Pool.Rent(graph.Width, graph.Height, Format.B8G8R8A8_UNorm);
            var constants = new MatteConstants
            {
                MaskMode = (uint)shape.Mode,
                Invert = shape.Invert ? 1u : 0u,
                MaskOpacity = Math.Clamp(shape.Opacity, 0.0f, 1.0f),
                First = first ? 1u : 0u,
            };

            _views[0] = feathered.Resource;
            _views[1] = matte?.Resource ?? feathered.Resource;
            FullScreen(_shaders!.MatteCombine, combined.View, combined.Width, combined.Height, in constants, 2);

            Pool.Return(feathered);
            if (matte is not null)
            {
                Pool.Return(matte);
            }

            matte = combined;
            first = false;
        }

        return matte!;
    }

    /// <summary>A separable Gaussian on a coverage texture's alpha.</summary>
    private RenderTarget Feather(RenderTarget coverage, float sigma)
    {
        int radius = (int)Math.Ceiling(sigma * 3.0f);
        RenderTarget across = Pool.Rent(coverage.Width, coverage.Height, Format.B8G8R8A8_UNorm);
        RenderTarget down = Pool.Rent(coverage.Width, coverage.Height, Format.B8G8R8A8_UNorm);

        var horizontal = new MatteConstants { Direction = new Vector2(1.0f / coverage.Width, 0.0f), Sigma = sigma, Radius = radius };
        _views[0] = coverage.Resource;
        FullScreen(_shaders!.MatteBlur, across.View, across.Width, across.Height, in horizontal, 1);

        var vertical = new MatteConstants { Direction = new Vector2(0.0f, 1.0f / coverage.Height), Sigma = sigma, Radius = radius };
        _views[0] = across.Resource;
        FullScreen(_shaders.MatteBlur, down.View, down.Width, down.Height, in vertical, 1);

        Pool.Return(across);
        return down;
    }

    private static LayerKey? LayerKeyFor(RenderGraph graph, LayerNode layer)
    {
        if (!graph.CacheLayers)
        {
            return null;
        }

        string? identity = layer.Source switch
        {
            FrameLayerSource frame => frame.Identity,
            SolidLayerSource solid => FormattableString.Invariant($"solid:{solid.Color}"),
            _ => null,
        };

        return identity is null
            ? null
            : new LayerKey(identity, layer.Transform, layer.Crop, graph.Width, graph.Height, graph.Bicubic);
    }

    private void Clear(RenderTarget target, Vector4 color) =>
        _device.ImmediateContext.ClearRenderTargetView(target.View, new Color4(color.X, color.Y, color.Z, color.W));

    private void EnsureShaders()
    {
        int generation = ShaderLibrary.Generation;
        if (_shaders is not null && generation == _shaderGeneration)
        {
            return;
        }

        Shaders fresh;
        try
        {
            fresh = new Shaders(_device);
        }
        catch (RenderDeviceException exception) when (_shaders is not null)
        {
            // A hot reloaded shader that does not compile keeps the last good set until the next
            // edit, rather than failing every frame until somebody fixes the typo.
            _log.Error(exception, "A shader did not compile; keeping the last good set");
            _shaderGeneration = generation;
            return;
        }

        _shaders?.Dispose();
        _shaders = fresh;
        _shaderGeneration = generation;
    }

    private void FullScreen<T>(ID3D11PixelShader pixel, ID3D11RenderTargetView target, int width, int height, in T constants, int views)
        where T : unmanaged =>
        Draw(_shaders!.FullScreenVertex, pixel, target, width, height, in constants, views, PrimitiveTopology.TriangleList, 3);

    private void Draw<T>(
        ID3D11VertexShader vertex,
        ID3D11PixelShader pixel,
        ID3D11RenderTargetView target,
        int width,
        int height,
        in T constants,
        int views,
        PrimitiveTopology topology,
        int vertices,
        ID3D11BlendState? blend = null)
        where T : unmanaged
    {
        ID3D11DeviceContext context = _device.ImmediateContext;
        ID3D11Buffer buffer = ConstantBuffer<T>();

        MappedSubresource mapped = context.Map(buffer, 0, MapMode.WriteDiscard);
        try
        {
            unsafe
            {
                *(T*)mapped.DataPointer = constants;
            }
        }
        finally
        {
            context.Unmap(buffer, 0);
        }

        for (int slot = views; slot < _views.Length; slot++)
        {
            _views[slot] = null;
        }

        context.ClearState();
        context.IASetPrimitiveTopology(topology);
        context.VSSetShader(vertex);
        context.VSSetConstantBuffer(0, buffer);
        context.PSSetShader(pixel);
        context.PSSetConstantBuffer(0, buffer);
        context.PSSetShaderResources(0, _views!);
        context.PSSetSamplers(0, _samplers);
        context.OMSetRenderTargets(target);
        if (blend is not null)
        {
            context.OMSetBlendState(blend);
        }

        context.RSSetViewport(new Viewport(0, 0, width, height, 0.0f, 1.0f));
        context.Draw((uint)vertices, 0);
        context.ClearState();

        Array.Clear(_views);
    }

    private ID3D11Buffer ConstantBuffer<T>()
        where T : unmanaged
    {
        if (_constants.TryGetValue(typeof(T), out ID3D11Buffer? existing))
        {
            return existing;
        }

        // Constant buffers are sized in sixteen byte registers.
        uint size = (uint)((Unsafe.SizeOf<T>() + 15) / 16 * 16);
        ID3D11Buffer buffer = _device.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = size,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        _constants[typeof(T)] = buffer;
        return buffer;
    }

    private ID3D11SamplerState Sampler(Filter filter, TextureAddressMode address) =>
        _device.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = filter,
            AddressU = address,
            AddressV = address,
            AddressW = address,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });

    /// <summary>The compiled passes, rebuilt together when a shader file is edited.</summary>
    private sealed class Shaders : IDisposable
    {
        public Shaders(RenderDevice device)
        {
            FullScreenVertex = ShaderLibrary.VertexShader(device, "Source.hlsl", "VsMain");
            SourcePixel = ShaderLibrary.PixelShader(device, "Source.hlsl", "PsMain");
            TransformVertex = ShaderLibrary.VertexShader(device, "Transform.hlsl", "VsMain");
            TransformPixel = ShaderLibrary.PixelShader(device, "Transform.hlsl", "PsMain");
            CompositePixel = ShaderLibrary.PixelShader(device, "Composite.hlsl", "PsMain");
            MatteBlur = ShaderLibrary.PixelShader(device, "Matte.hlsl", "PsBlur");
            MatteCombine = ShaderLibrary.PixelShader(device, "Matte.hlsl", "PsCombine");
            OutputPixel = ShaderLibrary.PixelShader(device, "Output.hlsl", "PsMain");
        }

        public ID3D11VertexShader FullScreenVertex { get; }

        public ID3D11PixelShader SourcePixel { get; }

        public ID3D11VertexShader TransformVertex { get; }

        public ID3D11PixelShader TransformPixel { get; }

        public ID3D11PixelShader CompositePixel { get; }

        public ID3D11PixelShader MatteBlur { get; }

        public ID3D11PixelShader MatteCombine { get; }

        public ID3D11PixelShader OutputPixel { get; }

        public void Dispose()
        {
            OutputPixel.Dispose();
            MatteCombine.Dispose();
            MatteBlur.Dispose();
            CompositePixel.Dispose();
            TransformPixel.Dispose();
            TransformVertex.Dispose();
            SourcePixel.Dispose();
            FullScreenVertex.Dispose();
        }
    }

    /// <summary>Hands effects a context, made once per quality rather than per frame.</summary>
    private sealed class EffectContextFactory(RenderDevice device, RenderTargetPool pool)
    {
        private EffectContext? _last;

        public EffectContext For(float qualityScale)
        {
            if (_last is null || _last.QualityScale != qualityScale)
            {
                _last = new EffectContext(device, pool, qualityScale);
            }

            return _last;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceConstants
    {
        public Vector3 MatrixRow0;
        public float SampleScale;
        public Vector3 MatrixRow1;
        public float LumaOffset;
        public Vector3 MatrixRow2;
        public float ChromaOffset;
        public float LumaRange;
        public float ChromaRange;
        public uint Transfer;
        public uint Layout;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TransformConstants
    {
        public Vector4 MatrixRow0;
        public Vector4 MatrixRow1;
        public Vector2 TargetSize;
        public Vector2 SourceSize;
        public Vector4 Crop;
        public Vector2 TextureSize;
        public uint Bicubic;
        public float Opacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositeConstants
    {
        public float Opacity;
        public uint Mode;
        public uint HasMatte;
        public float Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MatteConstants
    {
        public Vector2 Direction;
        public float Sigma;
        public int Radius;
        public uint MaskMode;
        public uint Invert;
        public float MaskOpacity;
        public uint First;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputConstants
    {
        public Vector4 Background;
        public uint Encoding;
        public uint DitherLevels;
        public uint KeepAlpha;
        public float Padding;
    }
}
