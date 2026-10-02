namespace JazzHands.Core.Model;

/// <summary>
/// What makes a clip a 3D layer (Phase 47): its depth, its turn about the X and Y axes, and how it
/// takes light. A clip without one is a flat layer.
/// </summary>
/// <remarks>
/// Space is sequence pixels with the origin at the frame centre, x to the right, y down and z away
/// from the viewer. The clip's own transform still says where it is across the frame, how big it
/// is, its turn about Z and its pivot; this adds the third dimension. A 3D layer at depth 0 with no
/// turn about X or Y, seen by the default camera, sits exactly where it would as a flat layer.
/// Animatable values are null at their defaults, as the clip's own are, and are read and written
/// through <see cref="Effects.ParamTargets"/> by their names (<c>transform.z</c>,
/// <c>material.diffuse</c>).
/// </remarks>
/// <param name="Z">Depth in sequence pixels, away from the viewer; negative comes towards the camera.</param>
/// <param name="RotationX">Degrees about the horizontal axis.</param>
/// <param name="RotationY">Degrees about the vertical axis.</param>
/// <param name="AcceptsLights">Lit by the scene's lights; when false the picture shows as it is.</param>
/// <param name="CastsShadows">Throws a shadow from lights that cast them.</param>
/// <param name="AcceptsShadows">Darkened by shadows other layers throw on it.</param>
/// <param name="Ambient">How much ambient light it takes, 0 to 1.</param>
/// <param name="Diffuse">How much direct light it scatters, 0 to 1.</param>
/// <param name="Specular">How bright its highlights are, 0 to 1.</param>
/// <param name="Roughness">How spread its highlights are, 0 (a mirror) to 1 (matte).</param>
public sealed record Layer3D(
    AnimatedValue? Z = null,
    AnimatedValue? RotationX = null,
    AnimatedValue? RotationY = null,
    bool AcceptsLights = true,
    bool CastsShadows = false,
    bool AcceptsShadows = true,
    AnimatedValue? Ambient = null,
    AnimatedValue? Diffuse = null,
    AnimatedValue? Specular = null,
    AnimatedValue? Roughness = null) : IEquatable<Layer3D>
{
    /// <summary>A 3D layer with everything at its default.</summary>
    public static Layer3D Default { get; } = new();
}

/// <summary>
/// The generator types that are not pictures but parts of a 3D scene (Phase 47): a camera and a
/// light. Their clips sit on video tracks so they have a time and keyframes, and draw nothing.
/// </summary>
public static class SceneObjects
{
    /// <summary>A camera: where the 3D layers are seen from.</summary>
    public const string Camera = "3d.camera";

    /// <summary>A light: ambient, point, spot or directional.</summary>
    public const string Light = "3d.light";

    /// <summary>Extruded, bevelled text (Phase 48).</summary>
    public const string Text = "3d.text";

    /// <summary>A solid: cube, sphere, cylinder, cone, torus or plane (Phase 48).</summary>
    public const string Shape = "3d.shape";

    /// <summary>A glTF 2.0 model from a file (Phase 48).</summary>
    public const string Model = "3d.model";

    /// <summary>True for a camera or a light.</summary>
    public static bool Is(string? generatorId) => generatorId is Camera or Light;

    /// <summary>True for a generator whose picture is geometry in a 3D scene: text, a shape or a model. Its clip is always 3D.</summary>
    public static bool IsMesh(string? generatorId) => generatorId is Text or Shape or Model;

    /// <summary>True for a clip that is a camera or a light.</summary>
    public static bool Is(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return Is(clip.GeneratorId);
    }
}
