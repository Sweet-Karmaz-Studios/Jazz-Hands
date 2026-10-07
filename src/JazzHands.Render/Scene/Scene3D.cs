using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JazzHands.Render.Compositing;
using JazzHands.Render.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace JazzHands.Render.Scene;

/// <summary>
/// Draws a 3D scene (Phases 47 and 48): solid meshes first, then its layers' canvases as quads
/// and any translucent meshes back to front, through the camera with a depth buffer, lit,
/// shadowed, and blurred by depth of field. One per compositor.
/// </summary>
/// <remarks>
/// Shadow maps are one float array, a slice for each spot or directional light that casts shadows
/// and six for a point light, kept and grown as scenes need more. The depth buffers are kept per
/// size. Meshes and their pictures go to the GPU the first time they are drawn and are kept while
/// they are drawn, dropped after a while unused. Everything else comes from the compositor's pool
/// and goes back within the frame. Thread affine to the device's immediate context, like the
/// compositor.
/// </remarks>
public sealed class Scene3D : IDisposable
{
    /// <summary>The most lights a scene takes; any more are left out.</summary>
    public const int MaxLights = 8;

    /// <summary>The most lights that cast shadows.</summary>
    public const int MaxShadowLights = 4;

    /// <summary>Each shadow map's width and height, in texels.</summary>
    public const int ShadowMapSize = 1024;

    private const int MaxSlices = 24;

    /// <summary>What a shadow map holds where nothing is: farther than anything.</summary>
    private const float Nothing = 1e30f;

    private readonly RenderDevice _device;
    private readonly RenderTargetPool _pool;
    private readonly ID3D11SamplerState[] _samplers;
    private readonly ID3D11SamplerState _anisotropic;
    private readonly ID3D11SamplerState _anisotropicWrap;
    private readonly ID3D11RasterizerState _meshFront;
    private readonly ID3D11RasterizerState _meshBoth;
    private readonly ID3D11Buffer _materialConstants;
    private readonly Dictionary<MeshData, GpuMesh> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(MeshTexture Texture, bool Srgb), GpuTexture> _textures = [];
    private long _frame;
    private readonly Dictionary<(int Width, int Height), Stack<MipCanvas>> _mips = [];
    private readonly ID3D11DepthStencilState _depthTest;
    private readonly ID3D11RasterizerState _bothSides;
    private readonly ID3D11BlendState _over;
    private readonly ID3D11Buffer _layerConstants;
    private readonly ID3D11Buffer _lightConstants;
    private readonly ID3D11Buffer _shadowConstants;
    private readonly ID3D11Buffer _dofConstants;

    private Passes? _passes;
    private int _generation = -1;
    private (int Width, int Height, ID3D11Texture2D Texture, ID3D11DepthStencilView View)? _depth;
    private ShadowArray? _shadows;
    private bool _disposed;

    /// <summary>Creates the scene renderer on a device, sharing the compositor's pool and samplers.</summary>
    internal Scene3D(RenderDevice device, RenderTargetPool pool, ID3D11SamplerState[] samplers)
    {
        _device = device;
        _pool = pool;
        _anisotropic = device.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.Anisotropic,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 16,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });
        _anisotropicWrap = device.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.Anisotropic,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 16,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });
        _samplers = [.. samplers, _anisotropic, _anisotropicWrap];

        // Meshes are wound as glTF winds them, counter-clockwise on screen from the front.
        _meshFront = device.Device.CreateRasterizerState(new RasterizerDescription(CullMode.Back, FillMode.Solid) { FrontCounterClockwise = true });
        _meshBoth = device.Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { FrontCounterClockwise = true });

        // Reversed depth: nearer is greater. Equal passes, so of two coplanar layers the one drawn
        // later (the higher track) shows, as it would flat.
        _depthTest = device.Device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.GreaterEqual));
        _bothSides = device.Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));

        // Colour premultiplied over; the depth for depth of field written as it is.
        var blend = new BlendDescription(Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha)
        {
            IndependentBlendEnable = true,
        };
        blend.RenderTarget[1] = new RenderTargetBlendDescription
        {
            BlendEnable = false,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.Zero,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.Zero,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _over = device.Device.CreateBlendState(blend);

        _layerConstants = Buffer<LayerConstants>();
        _lightConstants = Buffer<LightBlock>();
        _shadowConstants = Buffer<ShadowBlock>();
        _dofConstants = Buffer<DepthOfFieldConstants>();
        _materialConstants = Buffer<MaterialConstants>();
    }

    /// <summary>Layers drawn, for diagnostics and tests.</summary>
    public long LayersDrawn { get; private set; }

    /// <summary>Shadow map slices drawn, for diagnostics and tests.</summary>
    public long ShadowSlicesDrawn { get; private set; }

    /// <summary>Mesh parts drawn, for diagnostics and tests.</summary>
    public long MeshPartsDrawn { get; private set; }

    /// <summary>Meshes held on the GPU, for tests of what the cache keeps.</summary>
    internal int MeshesHeld => _meshes.Count;

    /// <summary>
    /// Draws a scene into a new frame-sized target of premultiplied linear light.
    /// </summary>
    /// <param name="scene">The scene.</param>
    /// <param name="canvases">Each layer's canvas, in the scene's order.</param>
    /// <param name="width">The output width.</param>
    /// <param name="height">The output height.</param>
    /// <param name="outputScale">Output pixels per sequence pixel.</param>
    /// <param name="bicubic">Sample a canvas seen at about its own size with the compositor's bicubic filter, as a flat layer is.</param>
    /// <param name="environment">An environment light's picture in linear light, or null.</param>
    /// <returns>A target rented from the pool; the caller returns it.</returns>
    public RenderTarget Draw(SceneLayerSource scene, IReadOnlyList<RenderTarget> canvases, int width, int height, float outputScale, bool bicubic = true, RenderTarget? environment = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(canvases);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsurePasses();

        SceneCamera camera = scene.Camera;
        int[] order = DrawingOrder(scene);
        bool anyLight = !scene.Lights.IsDefaultOrEmpty;

        // Lights, and the shadow maps of those that cast shadows.
        var lights = new LightBlock();
        var slices = new ShadowBlock();
        var shadowed = new List<(int Light, int FirstSlice)>();
        int lightCount = Math.Min(anyLight ? scene.Lights.Length : 0, MaxLights);
        _frame++;
        bool anyCaster = scene.Layers.Any(layer => layer.Material.CastsShadows && layer.Opacity > 0.0f)
            || scene.Meshes.Any(mesh => mesh.CastsShadows && mesh.Opacity > 0.0f);
        int sliceCount = 0;
        for (int index = 0; index < lightCount; index++)
        {
            SceneLight light = scene.Lights[index];
            int first = -1;
            int needs = light.Kind == SceneLightKind.Point ? 6 : 1;
            if (light.CastsShadows && light.Kind is not (SceneLightKind.Ambient or SceneLightKind.Environment) && anyCaster
                && shadowed.Count < MaxShadowLights && sliceCount + needs <= MaxSlices)
            {
                first = sliceCount;
                sliceCount += needs;
                shadowed.Add((index, first));
            }

            if (first >= 0)
            {
                IReadOnlyList<Matrix4x4> matrices = ShadowMatrices(light, scene);
                for (int slice = 0; slice < matrices.Count; slice++)
                {
                    slices[first + slice] = matrices[slice];
                }
            }

            lights[index] = Pack(light, first, ShadowTexel(light, scene));
        }

        if (sliceCount > 0)
        {
            DrawShadows(scene, canvases, shadowed, in slices, sliceCount);
        }

        RenderTarget color = _pool.Rent(width, height);
        RenderTarget? depth = camera.DepthOfField ? _pool.Rent(width, height, Format.R32_Float) : null;
        ID3D11DepthStencilView depthBuffer = DepthBuffer(width, height);

        ID3D11DeviceContext context = _device.ImmediateContext;
        context.ClearRenderTargetView(color.View, new Color4(0.0f, 0.0f, 0.0f, 0.0f));
        if (depth is not null)
        {
            context.ClearRenderTargetView(depth.View, new Color4(Nothing, Nothing, Nothing, Nothing));
        }

        context.ClearDepthStencilView(depthBuffer, DepthStencilClearFlags.Depth, 0.0f, 0);

        Upload(_lightConstants, in lights);
        Upload(_shadowConstants, in slices);

        // Each canvas with its mipmaps, for a layer seen smaller than it was drawn or slantwise.
        var mipped = new MipCanvas?[canvases.Count];
        for (int index = 0; index < canvases.Count; index++)
        {
            if (scene.Layers[index].Opacity > 0.0f)
            {
                mipped[index] = Mipped(canvases[index]);
            }
        }

        // An environment light's picture, with its mipmaps for blurred reflections.
        MipCanvas? surround = environment is null ? null : Mipped(environment);

        Matrix4x4 viewProjection = camera.ViewProjection;
        var frame = new Frame(camera, viewProjection, lightCount, anyLight, surround?.Resource, width, height, color, depth, depthBuffer);

        foreach (SceneMesh mesh in scene.Meshes)
        {
            if (!mesh.IsTranslucent && mesh.Opacity > 0.0f)
            {
                DrawMesh(frame, mesh);
            }
        }

        // Back to front by the depth of each one's centre; of two at the same depth, layers in
        // track order, then meshes.
        var translucent = new List<(float Depth, int Index, bool Mesh)>();
        foreach (int index in order)
        {
            translucent.Add((camera.Depth(scene.Layers[index].Centre), index, false));
        }

        for (int index = 0; index < scene.Meshes.Length; index++)
        {
            if (scene.Meshes[index].IsTranslucent)
            {
                translucent.Add((camera.Depth(scene.Meshes[index].Centre), index, true));
            }
        }

        foreach ((_, int index, bool isMesh) in translucent.OrderByDescending(item => item.Depth).ThenBy(item => item.Mesh).ThenBy(item => order.AsSpan().IndexOf(item.Index)))
        {
            if (isMesh)
            {
                DrawMesh(frame, scene.Meshes[index]);
                continue;
            }

            SceneLayer layer = scene.Layers[index];
            if (layer.Opacity <= 0.0f)
            {
                continue;
            }

            SceneMaterial material = layer.Material;
            var constants = new LayerConstants
            {
                World = layer.World,
                ViewProjection = viewProjection,
                CameraPosition = new Vector4(camera.Position, 1.0f),
                CameraForward = new Vector4(camera.Forward, 0.0f),
                CanvasSize = layer.CanvasSize,
                Opacity = Math.Clamp(layer.Opacity, 0.0f, 1.0f),
                Lit = anyLight && material.AcceptsLights ? 1u : 0u,
                Ambient = material.Ambient,
                Diffuse = material.Diffuse,
                Specular = material.Specular,
                Roughness = material.Roughness,
                AcceptsShadows = material.AcceptsShadows ? 1u : 0u,
                LightCount = (uint)lightCount,
                Bicubic = bicubic ? 1u : 0u,
                HasEnvironment = surround is null ? 0u : 1u,
            };
            Upload(_layerConstants, in constants);

            context.ClearState();
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            context.VSSetShader(_passes!.LayerVertex);
            context.VSSetConstantBuffer(0, _layerConstants);
            context.PSSetShader(_passes.LayerPixel);
            context.PSSetConstantBuffers(0, [_layerConstants, _lightConstants, _shadowConstants]);
            ID3D11ShaderResourceView?[] views = [mipped[index]!.Resource, _shadows?.Resource];
            context.PSSetShaderResources(0, views!);
            context.PSSetShaderResource(11, surround?.Resource!);
            context.PSSetSamplers(0, _samplers);
            context.RSSetState(_bothSides);
            Bind(frame);
            context.Draw(4, 0);
            LayersDrawn++;
        }

        if (surround is not null)
        {
            _mips[(surround.Width, surround.Height)].Push(surround);
        }

        Trim();
        context.ClearState();
        foreach (MipCanvas? canvas in mipped)
        {
            if (canvas is not null)
            {
                _mips[(canvas.Width, canvas.Height)].Push(canvas);
            }
        }

        if (depth is null)
        {
            return color;
        }

        float maxRadius = MathF.Min(camera.Aperture * 2.0f, 96.0f) * outputScale;
        if (maxRadius < 0.5f)
        {
            _pool.Return(depth);
            return color;
        }

        var dof = new DepthOfFieldConstants
        {
            TexelSize = new Vector2(1.0f / width, 1.0f / height),
            Focus = camera.Focus,
            Aperture = camera.Aperture * outputScale,
            MaxRadius = maxRadius,
            TileSize = DepthOfFieldTile,
            FrameWidth = (uint)width,
            FrameHeight = (uint)height,
        };
        Upload(_dofConstants, in dof);

        // The largest blur in each tile, then the gather, as wide at each pixel as its tiles say.
        int tilesWide = (width + DepthOfFieldTile - 1) / DepthOfFieldTile;
        int tilesHigh = (height + DepthOfFieldTile - 1) / DepthOfFieldTile;
        RenderTarget tiles = _pool.Rent(tilesWide, tilesHigh, Format.R32_Float);
        FullScreen(_passes!.CocTiles, tiles, [null, depth.Resource]);

        RenderTarget blurred = _pool.Rent(width, height);
        FullScreen(_passes.DepthOfField, blurred, [color.Resource, depth.Resource, tiles.Resource]);

        _pool.Return(tiles);
        _pool.Return(color);
        _pool.Return(depth);
        return blurred;
    }

    /// <summary>Tiles of the largest blur are this many pixels across.</summary>
    private const int DepthOfFieldTile = 16;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (MipCanvas canvas in _mips.Values.SelectMany(stack => stack))
        {
            canvas.Dispose();
        }

        foreach (GpuMesh mesh in _meshes.Values)
        {
            mesh.Dispose();
        }

        foreach (GpuTexture texture in _textures.Values)
        {
            texture.Dispose();
        }

        _materialConstants.Dispose();
        _meshBoth.Dispose();
        _meshFront.Dispose();
        _anisotropicWrap.Dispose();
        _anisotropic.Dispose();
        _passes?.Dispose();
        _shadows?.Dispose();
        if (_depth is { } depth)
        {
            depth.View.Dispose();
            depth.Texture.Dispose();
        }

        _dofConstants.Dispose();
        _shadowConstants.Dispose();
        _lightConstants.Dispose();
        _layerConstants.Dispose();
        _over.Dispose();
        _bothSides.Dispose();
        _depthTest.Dispose();
    }

    /// <summary>
    /// The order the layers are drawn in: farthest from the camera first, by the depth of each
    /// canvas's centre; of two at the same depth, the lower track first.
    /// </summary>
    internal static int[] DrawingOrder(SceneLayerSource scene)
    {
        SceneCamera camera = scene.Camera;
        return [.. Enumerable.Range(0, scene.Layers.Length).OrderByDescending(index => camera.Depth(scene.Layers[index].Centre)).ThenBy(index => index)];
    }

    /// <summary>
    /// The shadow map matrices of a light, one per slice, written into the block from its first
    /// slice: a spot's cone, a point's six cube faces, or a box round the scene for a directional.
    /// </summary>
    internal static IReadOnlyList<Matrix4x4> ShadowMatrices(SceneLight light, SceneLayerSource scene)
    {
        switch (light.Kind)
        {
            case SceneLightKind.Spot:
            {
                (Vector3 right, Vector3 down, Vector3 forward) = SceneMath.LookAt(light.Position, light.Position + light.Direction);
                float scale = 1.0f / MathF.Tan(SpotHalfAngle(light));
                return [SceneMath.View(light.Position, right, down, forward) * SceneMath.Perspective(scale, scale, SceneCamera.Near)];
            }

            case SceneLightKind.Point:
            {
                // Each face a little wider than a quarter turn, so filtering near an edge stays on it.
                float scale = 1.0f / MathF.Tan(SceneMath.Radians(PointFaceHalfAngle));
                Vector3[] axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
                return [.. axes.Select(axis =>
                {
                    (Vector3 right, Vector3 down, Vector3 forward) = SceneMath.LookAt(light.Position, light.Position + axis);
                    return SceneMath.View(light.Position, right, down, forward) * SceneMath.Perspective(scale, scale, SceneCamera.Near);
                })];
            }

            default:
            {
                (Vector3 right, Vector3 down, Vector3 forward) = SceneMath.LookAt(light.Position, light.Position + light.Direction);
                Matrix4x4 view = SceneMath.View(light.Position, right, down, forward);
                (Vector3 low, Vector3 high) = Bounds(scene, view);
                Vector2 half = Vector2.Max(new Vector2(high.X - low.X, high.Y - low.Y) * 0.51f, new Vector2(1.0f));
                var centre = new Vector2(low.X + high.X, low.Y + high.Y) / 2.0f;
                return [view * SceneMath.Orthographic(centre, half, low.Z - 1.0f, high.Z + 1.0f)];
            }
        }
    }

    private const float PointFaceHalfAngle = 47.5f;

    private static float SpotHalfAngle(SceneLight light) =>
        SceneMath.Radians(Math.Min((light.ConeAngle / 2.0f) + 5.0f, 85.0f));

    /// <summary>Every layer's corners in a light's space, as a box.</summary>
    private static (Vector3 Low, Vector3 High) Bounds(SceneLayerSource scene, Matrix4x4 view)
    {
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        foreach (Vector3 corner in scene.Layers.SelectMany(layer => layer.Corners()).Concat(scene.Meshes.SelectMany(mesh => mesh.Corners())))
        {
            Vector3 seen = Vector3.Transform(corner, view);
            low = Vector3.Min(low, seen);
            high = Vector3.Max(high, seen);
        }

        return low.X > high.X ? (Vector3.Zero, Vector3.One) : (low, high);
    }

    /// <summary>
    /// World pixels a shadow texel covers: for a directional light, across its box; for a spot
    /// or point light, per pixel of distance from it.
    /// </summary>
    private static float ShadowTexel(SceneLight light, SceneLayerSource scene) => light.Kind switch
    {
        SceneLightKind.Spot => 2.0f * MathF.Tan(SpotHalfAngle(light)) / ShadowMapSize,
        SceneLightKind.Point => 2.0f * MathF.Tan(SceneMath.Radians(PointFaceHalfAngle)) / ShadowMapSize,
        SceneLightKind.Directional => DirectionalTexel(light, scene),
        _ => 0.0f,
    };

    private static float DirectionalTexel(SceneLight light, SceneLayerSource scene)
    {
        (Vector3 right, Vector3 down, Vector3 forward) = SceneMath.LookAt(light.Position, light.Position + light.Direction);
        (Vector3 low, Vector3 high) = Bounds(scene, SceneMath.View(light.Position, right, down, forward));
        return MathF.Max(high.X - low.X, high.Y - low.Y) * 1.02f / ShadowMapSize;
    }

    private static Light Pack(SceneLight light, int firstSlice, float texel)
    {
        float outer = SceneMath.Radians(light.ConeAngle / 2.0f);
        float inner = outer * (1.0f - Math.Clamp(light.ConeFeather, 0.0f, 1.0f));
        float kind = light.Kind switch
        {
            SceneLightKind.Ambient => 0.0f,
            SceneLightKind.Point => 1.0f,
            SceneLightKind.Spot => 2.0f,
            SceneLightKind.Environment => 4.0f,
            _ => 3.0f,
        };

        return new Light
        {
            PositionKind = new Vector4(light.Position, kind),
            DirectionCone = new Vector4(light.Direction, MathF.Cos(outer)),
            ColorInner = new Vector4(light.Color, MathF.Cos(Math.Min(inner, outer - 1e-4f))),
            Falloff = new Vector4((float)light.Falloff, light.Radius, light.FalloffDistance, 0.0f),
            Shadow = new Vector4(firstSlice, Math.Clamp(light.ShadowDarkness, 0.0f, 1.0f), light.ShadowSoftness, texel),
        };
    }

    private void DrawShadows(SceneLayerSource scene, IReadOnlyList<RenderTarget> canvases, List<(int Light, int FirstSlice)> shadowed, in ShadowBlock slices, int sliceCount)
    {
        if (_shadows is null || _shadows.Slices < sliceCount)
        {
            _shadows?.Dispose();
            _shadows = new ShadowArray(_device, Math.Max(sliceCount, 6));
        }

        ID3D11DeviceContext context = _device.ImmediateContext;
        foreach ((int lightIndex, int first) in shadowed)
        {
            SceneLight light = scene.Lights[lightIndex];
            int count = light.Kind == SceneLightKind.Point ? 6 : 1;
            for (int slice = first; slice < first + count; slice++)
            {
                context.ClearRenderTargetView(_shadows.Views[slice], new Color4(Nothing, Nothing, Nothing, Nothing));
                context.ClearDepthStencilView(_shadows.Depth, DepthStencilClearFlags.Depth, 0.0f, 0);

                foreach (SceneMesh mesh in scene.Meshes)
                {
                    if (mesh.CastsShadows && mesh.Opacity > 0.0f && mesh.Mesh.Indices.Length > 0)
                    {
                        DrawMeshShadow(mesh, light, slices[slice], slice);
                    }
                }

                for (int index = 0; index < scene.Layers.Length; index++)
                {
                    SceneLayer layer = scene.Layers[index];
                    if (!layer.Material.CastsShadows || layer.Opacity <= 0.0f)
                    {
                        continue;
                    }

                    var constants = new LayerConstants
                    {
                        World = layer.World,
                        ViewProjection = slices[slice],
                        CanvasSize = layer.CanvasSize,
                        Opacity = Math.Clamp(layer.Opacity, 0.0f, 1.0f),
                        ShadowKind = light.Kind == SceneLightKind.Directional ? 1u : 0u,
                        AlphaCut = 0.5f,
                        ShadowOrigin = new Vector4(light.Position, 1.0f),
                        ShadowDirection = new Vector4(light.Direction, 0.0f),
                    };
                    Upload(_layerConstants, in constants);

                    context.ClearState();
                    context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                    context.VSSetShader(_passes!.ShadowVertex);
                    context.VSSetConstantBuffer(0, _layerConstants);
                    context.PSSetShader(_passes.ShadowPixel);
                    context.PSSetConstantBuffer(0, _layerConstants);
                    context.PSSetShaderResources(0, [canvases[index].Resource]);
                    context.PSSetSamplers(0, _samplers);
                    context.RSSetState(_bothSides);
                    context.RSSetViewport(new Viewport(0, 0, ShadowMapSize, ShadowMapSize, 0.0f, 1.0f));
                    context.OMSetDepthStencilState(_depthTest, 0);
                    context.OMSetRenderTargets(_shadows.Views[slice], _shadows.Depth);
                    context.Draw(4, 0);
                }

                ShadowSlicesDrawn++;
            }
        }

        context.ClearState();
    }

    /// <summary>What every draw of a frame shares.</summary>
    private sealed record Frame(
        SceneCamera Camera,
        Matrix4x4 ViewProjection,
        int LightCount,
        bool AnyLight,
        ID3D11ShaderResourceView? Environment,
        int Width,
        int Height,
        RenderTarget Color,
        RenderTarget? Depth,
        ID3D11DepthStencilView DepthBuffer);

    /// <summary>Binds a frame's viewport, depth test, blending and targets.</summary>
    private void Bind(Frame frame)
    {
        ID3D11DeviceContext context = _device.ImmediateContext;
        context.RSSetViewport(new Viewport(0, 0, frame.Width, frame.Height, 0.0f, 1.0f));
        context.OMSetDepthStencilState(_depthTest, 0);
        context.OMSetBlendState(_over);
        if (frame.Depth is null)
        {
            context.OMSetRenderTargets(frame.Color.View, frame.DepthBuffer);
        }
        else
        {
            context.OMSetRenderTargets([frame.Color.View, frame.Depth.View], frame.DepthBuffer);
        }
    }

    /// <summary>Draws a mesh, part by part, each with its material.</summary>
    private void DrawMesh(Frame frame, SceneMesh mesh)
    {
        if (mesh.Mesh.Indices.Length == 0)
        {
            return;
        }

        GpuMesh gpu = Gpu(mesh.Mesh);
        var constants = new LayerConstants
        {
            World = mesh.World,
            ViewProjection = frame.ViewProjection,
            CameraPosition = new Vector4(frame.Camera.Position, 1.0f),
            CameraForward = new Vector4(frame.Camera.Forward, 0.0f),
            Opacity = Math.Clamp(mesh.Opacity, 0.0f, 1.0f),
            Lit = frame.AnyLight && mesh.AcceptsLights ? 1u : 0u,
            AcceptsShadows = mesh.AcceptsShadows ? 1u : 0u,
            LightCount = (uint)frame.LightCount,
            HasEnvironment = frame.Environment is null ? 0u : 1u,
        };
        Upload(_layerConstants, in constants);
        Matrix4x4 normals = NormalMatrix(mesh.World);

        ID3D11DeviceContext context = _device.ImmediateContext;
        foreach (MeshPart part in mesh.Mesh.Parts)
        {
            if (part.IndexCount <= 0)
            {
                continue;
            }

            PbrMaterial material = part.Material < mesh.Materials.Length ? mesh.Materials[part.Material] : PbrMaterial.Default;
            ID3D11ShaderResourceView?[] maps = Maps(material, out uint flags);
            var surface = Material(material, normals, flags);
            Upload(_materialConstants, in surface);

            context.ClearState();
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetIndexBuffer(gpu.Indices, Format.R32_UInt, 0);
            context.VSSetShader(_passes!.MeshVertex);
            context.VSSetConstantBuffer(0, _layerConstants);
            context.VSSetConstantBuffer(4, _materialConstants);
            context.VSSetShaderResource(5, gpu.VertexView);
            context.PSSetShader(_passes.MeshPixel);
            context.PSSetConstantBuffers(0, [_layerConstants, _lightConstants, _shadowConstants]);
            context.PSSetConstantBuffer(4, _materialConstants);
            ID3D11ShaderResourceView?[] shadows = [null, _shadows?.Resource];
            context.PSSetShaderResources(0, shadows!);
            context.PSSetShaderResources(6, maps!);
            context.PSSetShaderResource(11, frame.Environment!);
            context.PSSetSamplers(0, _samplers);
            context.RSSetState(material.DoubleSided ? _meshBoth : _meshFront);
            Bind(frame);
            context.DrawIndexed((uint)part.IndexCount, (uint)part.FirstIndex, 0);
            MeshPartsDrawn++;
        }
    }

    /// <summary>The material's constants: its factors, which pictures it has, and the normals' matrix.</summary>
    private static MaterialConstants Material(PbrMaterial material, Matrix4x4 normals, uint maps) => new()
    {
        NormalMatrix = normals,
        BaseColor = material.BaseColor,
        Emissive = material.Emissive,
        Metallic = Math.Clamp(material.Metallic, 0.0f, 1.0f),
        Roughness = Math.Clamp(material.Roughness, 0.0f, 1.0f),
        NormalScale = material.NormalScale,
        AlphaCutoff = material.AlphaCutoff,
        AlphaMode = (uint)material.Alpha,
        Maps = maps,
        DoubleSided = material.DoubleSided ? 1u : 0u,
        UvSets = (uint)material.UvSets,
    };

    /// <summary>The material's pictures at t6 to t10, and the flags saying which there are.</summary>
    private ID3D11ShaderResourceView?[] Maps(PbrMaterial material, out uint flags)
    {
        flags = 0;
        var maps = new ID3D11ShaderResourceView?[5];
        (MeshTexture? Texture, bool Srgb)[] slots =
        [
            (material.BaseColorTexture, true),
            (material.MetallicRoughnessTexture, false),
            (material.NormalTexture, false),
            (material.EmissiveTexture, true),
            (material.OcclusionTexture, false),
        ];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (slots[slot].Texture is { } texture)
            {
                maps[slot] = Gpu(texture, slots[slot].Srgb).View;
                flags |= 1u << slot;
            }
        }

        return maps;
    }

    /// <summary>Normals go through the inverse transpose of a matrix, which keeps them at right angles to a surface under any scale.</summary>
    private static Matrix4x4 NormalMatrix(Matrix4x4 world)
    {
        Matrix4x4 linear = world with { M41 = 0.0f, M42 = 0.0f, M43 = 0.0f };
        return Matrix4x4.Invert(linear, out Matrix4x4 inverse) ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;
    }

    /// <summary>
    /// A mesh's GPU copy, made the first time it is drawn. A bent mesh (Phase 49a) shares the copy
    /// of the mesh at rest it was bent from, its corners rewritten when another pose is drawn.
    /// </summary>
    private GpuMesh Gpu(MeshData mesh)
    {
        MeshData key = mesh.Rest ?? mesh;
        if (!_meshes.TryGetValue(key, out GpuMesh? gpu))
        {
            gpu = new GpuMesh(_device, mesh, bends: mesh.Rest is not null);
            _meshes[key] = gpu;
        }
        else if (mesh.Rest is not null && !ReferenceEquals(gpu.Shown, mesh))
        {
            gpu.Show(_device.ImmediateContext, mesh);
        }

        gpu.Used = _frame;
        return gpu;
    }

    /// <summary>A picture's GPU copy, with mipmaps, made the first time it is drawn.</summary>
    private GpuTexture Gpu(MeshTexture texture, bool srgb)
    {
        if (!_textures.TryGetValue((texture, srgb), out GpuTexture? gpu))
        {
            gpu = new GpuTexture(_device, texture, srgb);
            _textures[(texture, srgb)] = gpu;
        }

        gpu.Used = _frame;
        return gpu;
    }

    /// <summary>Drops the meshes and pictures not drawn for a while.</summary>
    private void Trim()
    {
        const long Unused = 240;
        foreach (MeshData stale in _meshes.Where(entry => _frame - entry.Value.Used > Unused).Select(entry => entry.Key).ToList())
        {
            _meshes[stale].Dispose();
            _meshes.Remove(stale);
        }

        foreach ((MeshTexture, bool) stale in _textures.Where(entry => _frame - entry.Value.Used > Unused).Select(entry => entry.Key).ToList())
        {
            _textures[stale].Dispose();
            _textures.Remove(stale);
        }
    }

    /// <summary>A depth of field pass over the whole of a target, its inputs from t2.</summary>
    private void FullScreen(ID3D11PixelShader pixel, RenderTarget target, ID3D11ShaderResourceView?[] inputs)
    {
        ID3D11DeviceContext context = _device.ImmediateContext;
        context.ClearState();
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_passes!.FullScreenVertex);
        context.PSSetShader(pixel);
        context.PSSetConstantBuffer(3, _dofConstants);
        context.PSSetShaderResources(2, inputs!);
        context.PSSetSamplers(0, _samplers);
        context.RSSetViewport(new Viewport(0, 0, target.Width, target.Height, 0.0f, 1.0f));
        context.OMSetRenderTargets(target.View);
        context.Draw(3, 0);
        context.ClearState();
    }

    /// <summary>A canvas copied into a texture with a full chain of mipmaps, rented for the frame.</summary>
    private MipCanvas Mipped(RenderTarget canvas)
    {
        if (!_mips.TryGetValue((canvas.Width, canvas.Height), out Stack<MipCanvas>? idle))
        {
            idle = new Stack<MipCanvas>();
            _mips[(canvas.Width, canvas.Height)] = idle;
        }

        MipCanvas mipped = idle.Count > 0 ? idle.Pop() : new MipCanvas(_device, canvas.Width, canvas.Height);
        ID3D11DeviceContext context = _device.ImmediateContext;
        context.CopySubresourceRegion(mipped.Texture, 0, 0, 0, 0, canvas.Texture, 0);
        context.GenerateMips(mipped.Resource);
        return mipped;
    }

    /// <summary>A mesh into one shadow slice: its distance from the light where it is, cut where a masked material is clear.</summary>
    private void DrawMeshShadow(SceneMesh mesh, SceneLight light, Matrix4x4 viewProjection, int slice)
    {
        GpuMesh gpu = Gpu(mesh.Mesh);
        var constants = new LayerConstants
        {
            World = mesh.World,
            ViewProjection = viewProjection,
            Opacity = Math.Clamp(mesh.Opacity, 0.0f, 1.0f),
            ShadowKind = light.Kind == SceneLightKind.Directional ? 1u : 0u,
            AlphaCut = 0.5f,
            ShadowOrigin = new Vector4(light.Position, 1.0f),
            ShadowDirection = new Vector4(light.Direction, 0.0f),
        };
        Upload(_layerConstants, in constants);

        ID3D11DeviceContext context = _device.ImmediateContext;
        foreach (MeshPart part in mesh.Mesh.Parts)
        {
            PbrMaterial material = part.Material < mesh.Materials.Length ? mesh.Materials[part.Material] : PbrMaterial.Default;
            ID3D11ShaderResourceView?[] maps = Maps(material, out uint flags);
            var surface = Material(material, Matrix4x4.Identity, flags & 1u);
            Upload(_materialConstants, in surface);

            context.ClearState();
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetIndexBuffer(gpu.Indices, Format.R32_UInt, 0);
            context.VSSetShader(_passes!.MeshShadowVertex);
            context.VSSetConstantBuffer(0, _layerConstants);
            context.VSSetShaderResource(5, gpu.VertexView);
            context.PSSetShader(_passes.MeshShadowPixel);
            context.PSSetConstantBuffer(0, _layerConstants);
            context.PSSetConstantBuffer(4, _materialConstants);
            context.PSSetShaderResource(6, maps[0]!);
            context.PSSetSamplers(0, _samplers);
            context.RSSetState(_meshBoth);
            context.RSSetViewport(new Viewport(0, 0, ShadowMapSize, ShadowMapSize, 0.0f, 1.0f));
            context.OMSetDepthStencilState(_depthTest, 0);
            context.OMSetRenderTargets(_shadows!.Views[slice], _shadows.Depth);
            context.DrawIndexed((uint)part.IndexCount, (uint)part.FirstIndex, 0);
        }
    }

    private ID3D11DepthStencilView DepthBuffer(int width, int height)
    {
        if (_depth is { } kept && kept.Width == width && kept.Height == height)
        {
            return kept.View;
        }

        if (_depth is { } old)
        {
            old.View.Dispose();
            old.Texture.Dispose();
        }

        ID3D11Texture2D texture = DepthTexture(_device, width, height);
        ID3D11DepthStencilView view = _device.Device.CreateDepthStencilView(texture);
        _depth = (width, height, texture, view);
        return view;
    }

    private static ID3D11Texture2D DepthTexture(RenderDevice device, int width, int height) =>
        device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });

    private void EnsurePasses()
    {
        int generation = ShaderLibrary.Generation;
        if (_passes is not null && generation == _generation)
        {
            return;
        }

        var fresh = new Passes(_device);
        _passes?.Dispose();
        _passes = fresh;
        _generation = generation;
    }

    private ID3D11Buffer Buffer<T>()
        where T : unmanaged =>
        _device.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)((Unsafe.SizeOf<T>() + 15) / 16 * 16),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

    private void Upload<T>(ID3D11Buffer buffer, in T value)
        where T : unmanaged
    {
        ID3D11DeviceContext context = _device.ImmediateContext;
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

    /// <summary>The shadow maps: one float array, a render target view per slice, one depth buffer for drawing any of them.</summary>
    private sealed class ShadowArray : IDisposable
    {
        public ShadowArray(RenderDevice device, int slices)
        {
            Slices = slices;
            Texture = device.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = ShadowMapSize,
                Height = ShadowMapSize,
                MipLevels = 1,
                ArraySize = (uint)slices,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            Views = [.. Enumerable.Range(0, slices).Select(slice => device.Device.CreateRenderTargetView(
                Texture,
                new RenderTargetViewDescription(Texture, RenderTargetViewDimension.Texture2DArray, Format.R32_Float, 0, (uint)slice, 1)))];
            Resource = device.Device.CreateShaderResourceView(Texture);
            DepthTexture = Scene3D.DepthTexture(device, ShadowMapSize, ShadowMapSize);
            Depth = device.Device.CreateDepthStencilView(DepthTexture);
        }

        public int Slices { get; }

        public ID3D11Texture2D Texture { get; }

        public ID3D11RenderTargetView[] Views { get; }

        public ID3D11ShaderResourceView Resource { get; }

        public ID3D11Texture2D DepthTexture { get; }

        public ID3D11DepthStencilView Depth { get; }

        public void Dispose()
        {
            Depth.Dispose();
            DepthTexture.Dispose();
            Resource.Dispose();
            foreach (ID3D11RenderTargetView view in Views)
            {
                view.Dispose();
            }

            Texture.Dispose();
        }
    }

    /// <summary>A canvas with mipmaps.</summary>
    private sealed class MipCanvas : IDisposable
    {
        public MipCanvas(RenderDevice device, int width, int height)
        {
            Width = width;
            Height = height;
            Texture = device.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 0,
                ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });
            Resource = device.Device.CreateShaderResourceView(Texture);
        }

        public int Width { get; }

        public int Height { get; }

        public ID3D11Texture2D Texture { get; }

        public ID3D11ShaderResourceView Resource { get; }

        public void Dispose()
        {
            Resource.Dispose();
            Texture.Dispose();
        }
    }

    private sealed class Passes : IDisposable
    {
        public Passes(RenderDevice device)
        {
            LayerVertex = ShaderLibrary.VertexShader(device, "Scene3D.hlsl", "VsLayer");
            LayerPixel = ShaderLibrary.PixelShader(device, "Scene3D.hlsl", "PsLayer");
            ShadowVertex = ShaderLibrary.VertexShader(device, "Scene3D.hlsl", "VsShadow");
            ShadowPixel = ShaderLibrary.PixelShader(device, "Scene3D.hlsl", "PsShadow");
            FullScreenVertex = ShaderLibrary.VertexShader(device, "Scene3D.hlsl", "VsFullScreen");
            DepthOfField = ShaderLibrary.PixelShader(device, "Scene3D.hlsl", "PsDepthOfField");
            CocTiles = ShaderLibrary.PixelShader(device, "Scene3D.hlsl", "PsCocTiles");
            MeshVertex = ShaderLibrary.VertexShader(device, "Scene3D.hlsl", "VsMesh");
            MeshPixel = ShaderLibrary.PixelShader(device, "Scene3D.hlsl", "PsMesh");
            MeshShadowVertex = ShaderLibrary.VertexShader(device, "Scene3D.hlsl", "VsMeshShadow");
            MeshShadowPixel = ShaderLibrary.PixelShader(device, "Scene3D.hlsl", "PsMeshShadow");
        }

        public ID3D11VertexShader MeshVertex { get; }

        public ID3D11PixelShader MeshPixel { get; }

        public ID3D11VertexShader MeshShadowVertex { get; }

        public ID3D11PixelShader MeshShadowPixel { get; }

        public ID3D11PixelShader CocTiles { get; }

        public ID3D11VertexShader LayerVertex { get; }

        public ID3D11PixelShader LayerPixel { get; }

        public ID3D11VertexShader ShadowVertex { get; }

        public ID3D11PixelShader ShadowPixel { get; }

        public ID3D11VertexShader FullScreenVertex { get; }

        public ID3D11PixelShader DepthOfField { get; }

        public void Dispose()
        {
            MeshShadowPixel.Dispose();
            MeshShadowVertex.Dispose();
            MeshPixel.Dispose();
            MeshVertex.Dispose();
            CocTiles.Dispose();
            DepthOfField.Dispose();
            FullScreenVertex.Dispose();
            ShadowPixel.Dispose();
            ShadowVertex.Dispose();
            LayerPixel.Dispose();
            LayerVertex.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LayerConstants
    {
        public Matrix4x4 World;
        public Matrix4x4 ViewProjection;
        public Vector4 CameraPosition;
        public Vector4 CameraForward;
        public Vector2 CanvasSize;
        public float Opacity;
        public uint Lit;
        public float Ambient;
        public float Diffuse;
        public float Specular;
        public float Roughness;
        public uint AcceptsShadows;
        public uint LightCount;
        public uint ShadowKind;
        public float AlphaCut;
        public Vector4 ShadowOrigin;
        public Vector4 ShadowDirection;
        public uint Bicubic;
        public uint HasEnvironment;
        public float LayerPad0;
        public float LayerPad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialConstants
    {
        public Matrix4x4 NormalMatrix;
        public Vector4 BaseColor;
        public Vector3 Emissive;
        public float Metallic;
        public float Roughness;
        public float NormalScale;
        public float AlphaCutoff;
        public uint AlphaMode;
        public uint Maps;
        public uint DoubleSided;
        public uint UvSets;
        public float MaterialPad1;
    }

    /// <summary>A mesh on the GPU: its corners as a structured buffer the vertex shader reads by index, and its triangles.</summary>
    private sealed class GpuMesh : IDisposable
    {
        public unsafe GpuMesh(RenderDevice device, MeshData mesh, bool bends = false)
        {
            int stride = Unsafe.SizeOf<MeshVertex>();
            Shown = mesh;
            fixed (MeshVertex* corners = mesh.Vertices)
            {
                Vertices = device.Device.CreateBuffer(
                    new BufferDescription
                    {
                        ByteWidth = (uint)(Math.Max(mesh.Vertices.Length, 1) * stride),
                        Usage = bends ? ResourceUsage.Default : ResourceUsage.Immutable,
                        BindFlags = BindFlags.ShaderResource,
                        MiscFlags = ResourceOptionFlags.BufferStructured,
                        StructureByteStride = (uint)stride,
                    },
                    new SubresourceData((IntPtr)corners));
            }

            VertexView = device.Device.CreateShaderResourceView(Vertices, new ShaderResourceViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)Math.Max(mesh.Vertices.Length, 1) },
            });

            fixed (uint* indices = mesh.Indices)
            {
                Indices = device.Device.CreateBuffer(
                    new BufferDescription
                    {
                        ByteWidth = (uint)(Math.Max(mesh.Indices.Length, 1) * sizeof(uint)),
                        Usage = ResourceUsage.Immutable,
                        BindFlags = BindFlags.IndexBuffer,
                    },
                    new SubresourceData((IntPtr)indices));
            }
        }

        public ID3D11Buffer Vertices { get; }

        /// <summary>The pose whose corners the buffer holds.</summary>
        public MeshData Shown { get; private set; }

        /// <summary>Rewrites the corners for another pose of the same mesh.</summary>
        public unsafe void Show(ID3D11DeviceContext context, MeshData mesh)
        {
            fixed (MeshVertex* corners = mesh.Vertices)
            {
                context.UpdateSubresource(Vertices, 0, null, (IntPtr)corners, 0, 0);
            }

            Shown = mesh;
        }

        public ID3D11ShaderResourceView VertexView { get; }

        public ID3D11Buffer Indices { get; }

        public long Used { get; set; }

        public void Dispose()
        {
            Indices.Dispose();
            VertexView.Dispose();
            Vertices.Dispose();
        }
    }

    /// <summary>A material's picture on the GPU, sRGB or linear, with its mipmaps.</summary>
    private sealed class GpuTexture : IDisposable
    {
        public unsafe GpuTexture(RenderDevice device, MeshTexture texture, bool srgb)
        {
            Texture = device.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)texture.Width,
                Height = (uint)texture.Height,
                MipLevels = 0,
                ArraySize = 1,
                Format = srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });
            View = device.Device.CreateShaderResourceView(Texture);
            fixed (byte* texels = texture.Rgba)
            {
                device.ImmediateContext.UpdateSubresource(Texture, 0, null, (IntPtr)texels, (uint)(texture.Width * 4), 0);
            }

            device.ImmediateContext.GenerateMips(View);
        }

        public ID3D11Texture2D Texture { get; }

        public ID3D11ShaderResourceView View { get; }

        public long Used { get; set; }

        public void Dispose()
        {
            View.Dispose();
            Texture.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Light
    {
        public Vector4 PositionKind;
        public Vector4 DirectionCone;
        public Vector4 ColorInner;
        public Vector4 Falloff;
        public Vector4 Shadow;
    }

    [InlineArray(MaxLights)]
    private struct LightBlock
    {
        private Light _first;
    }

    [InlineArray(MaxSlices)]
    private struct ShadowBlock
    {
        private Matrix4x4 _first;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DepthOfFieldConstants
    {
        public Vector2 TexelSize;
        public float Focus;
        public float Aperture;
        public float MaxRadius;
        public uint TileSize;
        public uint FrameWidth;
        public uint FrameHeight;
    }
}
