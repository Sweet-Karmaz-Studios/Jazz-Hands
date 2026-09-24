using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Render.Effects;
using JazzHands.Render.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace JazzHands.Render.Compositing;

/// <summary>
/// What an effect is given to draw with: the device, the pool, the quality, where it is in time,
/// and a pass runner that binds everything the shader conventions promise.
/// </summary>
/// <remarks>
/// One per compositor, re-aimed at each effect before it runs. Pixel sizes in parameters are
/// sequence pixels; multiply them by <see cref="QualityScale"/> to get target texels, so the
/// preview at half size looks like the export. Thread affine, like the compositor.
/// </remarks>
public sealed class EffectContext : IDisposable
{
    private readonly ID3D11SamplerState[] _samplers;
    private readonly ID3D11ShaderResourceView?[] _views = new ID3D11ShaderResourceView?[4];
    private readonly Dictionary<(string File, string Entry), ID3D11PixelShader> _pixels = [];
    private readonly Dictionary<(string File, string Entry), ID3D11VertexShader> _vertices = [];
    private readonly Dictionary<Type, ID3D11Buffer> _buffers = [];
    private readonly ID3D11Buffer _common;
    private int _generation = -1;
    private EffectNode? _node;
    private Drawing2D? _drawing;

    /// <summary>Creates a context on a device, sharing a compositor's samplers.</summary>
    internal EffectContext(RenderDevice device, RenderTargetPool pool, ID3D11SamplerState[] samplers)
    {
        Device = device;
        Pool = pool;
        _samplers = samplers;
        _common = Buffer(Unsafe.SizeOf<CommonConstants>());
    }

    /// <summary>The device.</summary>
    public RenderDevice Device { get; }

    /// <summary>Where intermediate targets come from.</summary>
    public RenderTargetPool Pool { get; }

    /// <summary>Target texels per sequence pixel: 1 at Full, 0.5 at Half.</summary>
    public float QualityScale { get; private set; } = 1.0f;

    /// <summary>The time the frame is at, relative to the effect's owner: clip time for a clip's effects.</summary>
    public Flicks Time => _node?.LocalTime ?? Flicks.Zero;

    /// <summary>The folder the project file is in, which relative paths in parameters are from; empty for none.</summary>
    public string ProjectFolder { get; internal set; } = string.Empty;

    /// <summary>Direct2D and DirectWrite on this device, for shapes and text; made on first use.</summary>
    public Drawing2D Drawing => _drawing ??= new Drawing2D(Device);

    /// <summary>The sequence's frame rate, for anything that counts frames (a timecode).</summary>
    public Core.Time.Rational FrameRate => _node?.FrameRate ?? Core.Time.Rational.Fps30;

    /// <summary>How long the effect's owner lasts: the clip's duration, or the sequence's for a track.</summary>
    public Flicks OwnerLength => _node?.OwnerLength ?? Flicks.Zero;

    /// <summary>The frame's time on the sequence.</summary>
    public Flicks SequenceTime => _node?.SequenceTime ?? Flicks.Zero;

    /// <summary>How far through its owner the frame is, 0 at the start and 1 at the end.</summary>
    public float Progress => OwnerLength.Value > 0 ? Math.Clamp((float)((double)Time.Value / OwnerLength.Value), 0.0f, 1.0f) : 0.0f;

    /// <summary>A seed that is the same for this effect instance on every frame and every run.</summary>
    public int Seed => _node?.Seed ?? 0;

    /// <summary>The effect instance's identifier, for state an effect keeps per instance.</summary>
    public string InstanceId => _node?.InstanceId ?? string.Empty;

    /// <summary>
    /// A parameter of the running effect at another time, relative to its owner: motion blur
    /// wants the transform a fraction of a frame ago, and an echo wants the past.
    /// </summary>
    public ParamValue Eval(string name, Flicks time)
    {
        EffectNode node = _node ?? throw new InvalidOperationException("No effect is running.");
        ParamDescriptor descriptor = node.Descriptor.Param(name)
            ?? throw new ArgumentException($"'{node.Descriptor.TypeId}' has no parameter '{name}'.", nameof(name));
        return ParamEval.Eval(node.Model?.Parameter(name), descriptor, time);
    }

    /// <summary>A target from the pool; hand it back with <see cref="Return"/> before the effect ends.</summary>
    public RenderTarget Rent(int width, int height, Format format = Format.R16G16B16A16_Float) => Pool.Rent(width, height, format);

    /// <summary>Hands a rented target back.</summary>
    public void Return(RenderTarget target) => Pool.Return(target);

    /// <summary>Fills a target with one premultiplied colour.</summary>
    public void Clear(RenderTarget target, Vector4 color)
    {
        ArgumentNullException.ThrowIfNull(target);
        Device.ImmediateContext.ClearRenderTargetView(target.View, new Color4(color.X, color.Y, color.Z, color.W));
    }

    /// <summary>Copies one target into another of the same size and format.</summary>
    public void Copy(RenderTarget from, RenderTarget to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        Device.ImmediateContext.CopyResource(to.Texture, from.Texture);
    }

    /// <summary>
    /// Runs one pass into <paramref name="output"/>: the common constants at b0 (texel size,
    /// resolution, time in seconds, quality), <paramref name="constants"/> at b1, the inputs at
    /// t0 up, and the samplers at s0 to s2.
    /// </summary>
    public void Draw<T>(PassDescriptor pass, RenderTarget output, in T constants, params ReadOnlySpan<RenderTarget> inputs)
        where T : unmanaged
        => Draw(pass, output, in constants, inputs, []);

    /// <summary>
    /// Runs one pass with textures of the effect's own after the inputs: a baked curve, a 3D LUT.
    /// The inputs take t0 up and <paramref name="resources"/> the slots after them.
    /// </summary>
    public void Draw<T>(PassDescriptor pass, RenderTarget output, in T constants, ReadOnlySpan<RenderTarget> inputs, ReadOnlySpan<ID3D11ShaderResourceView> resources)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(output);
        if (inputs.Length + resources.Length > _views.Length)
        {
            throw new ArgumentException($"A pass reads at most {_views.Length} textures.", nameof(inputs));
        }

        Refresh();
        ID3D11DeviceContext context = Device.ImmediateContext;

        var common = new CommonConstants
        {
            TexelSize = new Vector2(1.0f / output.Width, 1.0f / output.Height),
            Resolution = new Vector2(output.Width, output.Height),
            Time = (float)Time.ToSeconds(),
            QualityScale = QualityScale,
        };

        Write(context, _common, in common);
        ID3D11Buffer buffer = BufferFor<T>();
        Write(context, buffer, in constants);

        for (int slot = 0; slot < _views.Length; slot++)
        {
            _views[slot] = slot < inputs.Length ? inputs[slot].Resource
                : slot < inputs.Length + resources.Length ? resources[slot - inputs.Length]
                : null;
        }

        context.ClearState();
        context.IASetPrimitiveTopology(pass.IsStrip ? PrimitiveTopology.TriangleStrip : PrimitiveTopology.TriangleList);
        context.VSSetShader(VertexShader(pass.File, pass.Vertex));
        context.VSSetConstantBuffers(0, [_common, buffer]);
        context.PSSetShader(PixelShader(pass.File, pass.Pixel));
        context.PSSetConstantBuffers(0, [_common, buffer]);
        context.PSSetShaderResources(0, _views!);
        context.PSSetSamplers(0, _samplers);
        context.OMSetRenderTargets(output.View);
        context.RSSetViewport(new Viewport(0, 0, output.Width, output.Height, 0.0f, 1.0f));
        context.Draw((uint)pass.Vertices, 0);
        context.ClearState();

        Array.Clear(_views);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _drawing?.Dispose();
        ReleaseShaders();
        foreach (ID3D11Buffer buffer in _buffers.Values)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
        _common.Dispose();
    }

    /// <summary>Aims the context at the next effect.</summary>
    internal void Begin(EffectNode node, float qualityScale)
    {
        _node = node;
        QualityScale = qualityScale;
    }

    /// <summary>Compiles every pass of an effect, so a shader that does not compile fails before the frame does.</summary>
    internal void Prepare(VideoEffect effect) => Prepare(effect.Passes);

    /// <summary>Compiles every pass of a generator.</summary>
    internal void Prepare(VideoGenerator generator) => Prepare(generator.Passes);

    private void Prepare(IEnumerable<PassDescriptor> passes)
    {
        Refresh();
        foreach (PassDescriptor pass in passes)
        {
            VertexShader(pass.File, pass.Vertex);
            PixelShader(pass.File, pass.Pixel);
        }
    }

    private void Refresh()
    {
        int generation = ShaderLibrary.Generation;
        if (generation != _generation)
        {
            ReleaseShaders();
            _generation = generation;
        }
    }

    private ID3D11PixelShader PixelShader(string file, string entry)
    {
        if (!_pixels.TryGetValue((file, entry), out ID3D11PixelShader? shader))
        {
            shader = ShaderLibrary.PixelShader(Device, file, entry);
            _pixels[(file, entry)] = shader;
        }

        return shader;
    }

    private ID3D11VertexShader VertexShader(string file, string entry)
    {
        if (!_vertices.TryGetValue((file, entry), out ID3D11VertexShader? shader))
        {
            shader = ShaderLibrary.VertexShader(Device, file, entry);
            _vertices[(file, entry)] = shader;
        }

        return shader;
    }

    private void ReleaseShaders()
    {
        foreach (ID3D11PixelShader shader in _pixels.Values)
        {
            shader.Dispose();
        }

        foreach (ID3D11VertexShader shader in _vertices.Values)
        {
            shader.Dispose();
        }

        _pixels.Clear();
        _vertices.Clear();
    }

    private ID3D11Buffer BufferFor<T>()
        where T : unmanaged
    {
        if (!_buffers.TryGetValue(typeof(T), out ID3D11Buffer? buffer))
        {
            buffer = Buffer(Unsafe.SizeOf<T>());
            _buffers[typeof(T)] = buffer;
        }

        return buffer;
    }

    private ID3D11Buffer Buffer(int size) =>
        Device.Device.CreateBuffer(new BufferDescription
        {
            // Constant buffers are sized in sixteen byte registers.
            ByteWidth = (uint)((Math.Max(size, 16) + 15) / 16 * 16),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

    private static void Write<T>(ID3D11DeviceContext context, ID3D11Buffer buffer, in T value)
        where T : unmanaged
    {
        MappedSubresource mapped = context.Map(buffer, 0, MapMode.WriteDiscard);
        try
        {
            unsafe
            {
                *(T*)mapped.DataPointer = value;
            }
        }
        finally
        {
            context.Unmap(buffer, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommonConstants
    {
        public Vector2 TexelSize;
        public Vector2 Resolution;
        public float Time;
        public float QualityScale;
        public Vector2 Padding;
    }
}
