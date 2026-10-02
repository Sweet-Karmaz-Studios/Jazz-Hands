using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Scene;

/// <summary>
/// A run of 3D layers drawn together (Phase 47): each layer's picture in a canvas of its own, set in
/// space, seen through one camera and lit by the scene's lights. The compositor draws it into one
/// frame-sized picture, laid over the stack underneath like any other layer.
/// </summary>
/// <param name="Layers">The layers, bottom track first; the scene sorts them by depth itself.</param>
/// <param name="Camera">What they are seen through.</param>
/// <param name="Lights">What lights them; none shows every layer unlit.</param>
public sealed record SceneLayerSource(ImmutableArray<SceneLayer> Layers, SceneCamera Camera, ImmutableArray<SceneLight> Lights) : LayerSource
{
    /// <summary>The geometry in the scene (Phase 48): text, shapes and models, each mesh with its place and materials.</summary>
    public ImmutableArray<SceneMesh> Meshes { get; init; } = [];
}

/// <summary>
/// A mesh in a scene (Phase 48): its geometry, where it is, and the materials of its parts.
/// </summary>
/// <param name="Mesh">The geometry, shared and cached.</param>
/// <param name="Materials">A material for each of the mesh's material slots.</param>
/// <param name="World">The mesh's own space to the world.</param>
/// <param name="Opacity">0 to 1.</param>
/// <param name="CastsShadows">Throws shadows.</param>
/// <param name="AcceptsShadows">Darkened by shadows.</param>
/// <param name="AcceptsLights">Lit; false shows its base colour as it is.</param>
public sealed record SceneMesh(MeshData Mesh, ImmutableArray<PbrMaterial> Materials, Matrix4x4 World, float Opacity = 1.0f, bool CastsShadows = true, bool AcceptsShadows = true, bool AcceptsLights = true)
{
    /// <summary>The centre of the mesh's box in the world.</summary>
    public Vector3 Centre => Vector3.Transform((Mesh.Bounds.Low + Mesh.Bounds.High) / 2.0f, World);

    /// <summary>The eight corners of its box in the world.</summary>
    public IEnumerable<Vector3> Corners()
    {
        (Vector3 low, Vector3 high) = Mesh.Bounds;
        for (int corner = 0; corner < 8; corner++)
        {
            yield return Vector3.Transform(new Vector3((corner & 1) == 0 ? low.X : high.X, (corner & 2) == 0 ? low.Y : high.Y, (corner & 4) == 0 ? low.Z : high.Z), World);
        }
    }

    /// <summary>True when it is drawn with blending, among the layers back to front; false when it is solid and drawn first.</summary>
    public bool IsTranslucent => Opacity < 1.0f || Materials.Any(material => material.Alpha == AlphaMode.Blend || material.BaseColor.W < 1.0f);
}

/// <summary>
/// One 3D layer: its picture drawn flat into a canvas, with its effects and masks in its own space,
/// and the matrix that sets the canvas in the world.
/// </summary>
/// <param name="Canvas">The layer drawn alone at the canvas's size: the clip's picture fitted and placed in the middle, its effects and masks applied, nothing moved.</param>
/// <param name="World">Canvas pixels (x, y, 0) to world space: sequence pixels from the frame centre, y down, z away.</param>
/// <param name="Opacity">0 to 1.</param>
/// <param name="Material">How it takes light and shadow.</param>
public sealed record SceneLayer(RenderGraph Canvas, Matrix4x4 World, float Opacity, SceneMaterial Material)
{
    /// <summary>The canvas's size in its own pixels.</summary>
    public Vector2 CanvasSize => new(Canvas.Width, Canvas.Height);

    /// <summary>The canvas's centre in the world.</summary>
    public Vector3 Centre => Vector3.Transform(new Vector3(CanvasSize / 2.0f, 0.0f), World);

    /// <summary>The canvas's four corners in the world: top left, top right, bottom left, bottom right.</summary>
    public IEnumerable<Vector3> Corners()
    {
        Vector2 size = CanvasSize;
        yield return Vector3.Transform(Vector3.Zero, World);
        yield return Vector3.Transform(new Vector3(size.X, 0.0f, 0.0f), World);
        yield return Vector3.Transform(new Vector3(0.0f, size.Y, 0.0f), World);
        yield return Vector3.Transform(new Vector3(size.X, size.Y, 0.0f), World);
    }
}

/// <summary>How a 3D layer takes light, each value evaluated at the frame.</summary>
/// <param name="AcceptsLights">Lit by the lights; false shows the picture as it is.</param>
/// <param name="CastsShadows">Throws shadows.</param>
/// <param name="AcceptsShadows">Darkened by shadows.</param>
/// <param name="Ambient">Share of ambient light taken, 0 to 1.</param>
/// <param name="Diffuse">Share of direct light scattered, 0 to 1.</param>
/// <param name="Specular">Reflectance of highlights, 0 to 1 (0.5 is plastic).</param>
/// <param name="Roughness">0 a mirror, 1 matte.</param>
public readonly record struct SceneMaterial(
    bool AcceptsLights = true,
    bool CastsShadows = false,
    bool AcceptsShadows = true,
    float Ambient = 1.0f,
    float Diffuse = 1.0f,
    float Specular = 0.5f,
    float Roughness = 0.5f);

/// <summary>
/// The camera at one frame: where it is, how it looks, and its lens.
/// </summary>
/// <param name="Position">Where it is in the world.</param>
/// <param name="Right">Its right, a unit vector.</param>
/// <param name="Down">Its down, a unit vector.</param>
/// <param name="Forward">The way it looks, a unit vector.</param>
/// <param name="Zoom">Its focal length in sequence pixels: something this far in front of it is seen at its own size.</param>
/// <param name="FrameSize">The sequence frame it frames, in sequence pixels.</param>
/// <param name="DepthOfField">True to blur by distance from the focus.</param>
/// <param name="Focus">Distance in front of it that is sharp, in sequence pixels.</param>
/// <param name="Aperture">The blur, in sequence pixels, of something infinitely far.</param>
public sealed record SceneCamera(
    Vector3 Position,
    Vector3 Right,
    Vector3 Down,
    Vector3 Forward,
    float Zoom,
    Vector2 FrameSize,
    bool DepthOfField = false,
    float Focus = 0.0f,
    float Aperture = 0.0f)
{
    /// <summary>The nearest anything is drawn, in sequence pixels in front of the camera.</summary>
    public const float Near = 1.0f;

    /// <summary>World to camera space: x right, y down, z ahead (row vectors, as System.Numerics).</summary>
    public Matrix4x4 View => SceneMath.View(Position, Right, Down, Forward);

    /// <summary>Camera space to clip space, with reversed infinite depth.</summary>
    public Matrix4x4 Projection => SceneMath.Perspective(Zoom / (FrameSize.X / 2.0f), Zoom / (FrameSize.Y / 2.0f), Near);

    /// <summary>World to clip space.</summary>
    public Matrix4x4 ViewProjection => View * Projection;

    /// <summary>Distance in front of the camera, along the way it looks.</summary>
    public float Depth(Vector3 point) => Vector3.Dot(point - Position, Forward);

    /// <summary>Where a point lands in the frame, in sequence pixels from the top left; null when it is behind the camera.</summary>
    public Vector2? Project(Vector3 point)
    {
        Vector3 relative = point - Position;
        float depth = Vector3.Dot(relative, Forward);
        if (depth < Near)
        {
            return null;
        }

        float scale = Zoom / depth;
        return new Vector2(Vector3.Dot(relative, Right) * scale, Vector3.Dot(relative, Down) * scale) + (FrameSize / 2.0f);
    }
}

/// <summary>What sort of light.</summary>
public enum SceneLightKind
{
    /// <summary>Even light everywhere, from no direction.</summary>
    Ambient,

    /// <summary>Every way from a point.</summary>
    Point,

    /// <summary>A cone from a point towards a target.</summary>
    Spot,

    /// <summary>Parallel light from far away, towards a target.</summary>
    Directional,

    /// <summary>Light from a picture all round (Phase 48): soft fill from its colours, and reflections in metal and polished surfaces.</summary>
    Environment,
}

/// <summary>How a point or spot light fades with distance.</summary>
public enum SceneFalloff
{
    /// <summary>It does not.</summary>
    None,

    /// <summary>Full strength to the radius, then smoothly to nothing over the falloff distance.</summary>
    Smooth,

    /// <summary>Full strength to the radius, then as the inverse square of the distance, as real light does.</summary>
    InverseSquare,
}

/// <summary>A light at one frame.</summary>
/// <param name="Kind">What sort.</param>
/// <param name="Color">Linear colour times intensity.</param>
/// <param name="Position">Where it is (point and spot), or where it shines from (directional).</param>
/// <param name="Direction">The way a spot or directional light shines, a unit vector.</param>
/// <param name="ConeAngle">A spot's full cone, in degrees.</param>
/// <param name="ConeFeather">How soft a spot's edge is, 0 to 1.</param>
/// <param name="Falloff">How it fades with distance.</param>
/// <param name="Radius">How far it reaches at full strength.</param>
/// <param name="FalloffDistance">How far past the radius a smooth falloff reaches nothing.</param>
/// <param name="CastsShadows">Layers that cast shadows throw them from this light.</param>
/// <param name="ShadowDarkness">How much of the light a shadow takes away, 0 to 1.</param>
/// <param name="ShadowSoftness">How soft a shadow's edge is, in sequence pixels.</param>
public sealed record SceneLight(
    SceneLightKind Kind,
    Vector3 Color,
    Vector3 Position = default,
    Vector3 Direction = default,
    float ConeAngle = 90.0f,
    float ConeFeather = 0.5f,
    SceneFalloff Falloff = SceneFalloff.None,
    float Radius = 500.0f,
    float FalloffDistance = 500.0f,
    bool CastsShadows = false,
    float ShadowDarkness = 1.0f,
    float ShadowSoftness = 0.0f)
{
    /// <summary>An environment light's picture, equirectangular: the media frame it is drawn from (Phase 48).</summary>
    public LayerSource? Image { get; init; }

    /// <summary>The picture's width and height in its own pixels.</summary>
    public Vector2 ImageSize { get; init; }
}
