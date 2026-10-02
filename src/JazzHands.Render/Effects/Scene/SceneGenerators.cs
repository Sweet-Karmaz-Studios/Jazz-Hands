using JazzHands.Core.Effects;
using JazzHands.Core.Model;

namespace JazzHands.Render.Effects.Scene;

/// <summary>
/// The camera the 3D layers are seen through (Phase 47). Its clip draws nothing; the topmost
/// camera under the playhead is the one a sequence uses. At rest it sees the frame exactly as it
/// is: a 3D layer at depth 0 with no turn sits where it would as a flat layer.
/// </summary>
/// <remarks>
/// It looks at its point of interest. The rest position is in front of the frame centre at the
/// distance where the default angle of view fits the frame's width; <c>dolly</c> moves it in from
/// there, <c>orbit</c> and <c>tilt</c> swing it round the point of interest, and a narrower
/// <c>angle</c> zooms in. Depth of field focuses at <c>focus</c> (the point of interest when 0)
/// and blurs by up to <c>aperture</c> pixels for something infinitely far away.
/// </remarks>
[Generator(SceneObjects.Camera, Name = "3D camera", Category = "3D", Description = "The camera 3D layers are seen through: move, orbit, zoom and focus.")]
[Param("position", ParamType.Point, Default = "0, 0", Unit = "px", Description = "Where the camera is across the frame, in sequence pixels from the centre; it keeps looking at its point of interest.")]
[Param("dolly", ParamType.Float, Default = "0", Min = -1000000, Max = 1000000, SliderMax = 3000, Unit = "px", Description = "How far the camera has moved in from its rest position, taking its point of interest with it; negative pulls back.")]
[Param("target", ParamType.Point, Default = "0, 0", Unit = "px", Label = "Target", Description = "What the camera looks at, its point of interest, across the frame.")]
[Param("target-z", ParamType.Float, Default = "0", Min = -1000000, Max = 1000000, SliderMax = 3000, Unit = "px", Label = "Target depth", Description = "The depth of what the camera looks at, its point of interest.")]
[Param("orbit", ParamType.Float, Default = "0", Min = -36000, Max = 36000, SliderMax = 360, Unit = "deg", Description = "Swings the camera round the point of interest, left and right.")]
[Param("tilt", ParamType.Float, Default = "0", Min = -89, Max = 89, Unit = "deg", Description = "Swings the camera round the point of interest, up (positive) and down.")]
[Param("roll", ParamType.Float, Default = "0", Min = -36000, Max = 36000, SliderMax = 360, Unit = "deg", Description = "Turns the camera about the way it looks, clockwise.")]
[Param("angle", ParamType.Float, Default = "39.6", Min = 1, Max = 170, Unit = "deg", Label = "Angle of view", Description = "How wide the camera sees, across the frame; narrower zooms in. 39.6 is a 50 mm lens on full frame.")]
[Param("depth-of-field", ParamType.Bool, Default = "false", Animatable = false, Description = "Blur what is nearer or farther than the focus distance.")]
[Param("focus", ParamType.Float, Default = "0", Min = 0, Max = 1000000, SliderMax = 5000, Unit = "px", Label = "Focus distance", Description = "How far from the camera the picture is sharp; 0 focuses on the point of interest.")]
[Param("aperture", ParamType.Float, Default = "20", Min = 0, Max = 500, SliderMax = 100, Unit = "px", Description = "How much is blurred out of focus: the blur, in pixels, of something infinitely far away.")]
public static class CameraGenerator
{
    /// <summary>The default angle of view, which sets how far the camera's rest position is from the frame.</summary>
    public const float RestAngle = 39.6f;
}

/// <summary>
/// A light for the 3D layers (Phase 47). Its clip draws nothing. With no light in a scene the
/// layers show their pictures unlit; once there is one, a layer that accepts lights is lit only by
/// the lights there are, so a scene without an ambient light has black shadows.
/// </summary>
[Generator(SceneObjects.Light, Name = "3D light", Category = "3D", Description = "Lights 3D layers: ambient, point, spot or directional, with shadows.")]
[Param("kind", ParamType.Enum, Default = "point", Choices = "point, spot, directional, ambient, environment", Animatable = false, Description = "A point shines every way from where it is; a spot in a cone at its target; directional from far away towards its target, like the sun; ambient everywhere evenly; environment from a picture all round, which metal and polished surfaces reflect.")]
[Param("color", ParamType.Color, Default = "#FFFFFF", Description = "The light's colour.")]
[Param("intensity", ParamType.Float, Default = "1", Min = 0, Max = 100, SliderMax = 3, Description = "How bright: 1 lights a white layer facing it to its full brightness.")]
[Param("position", ParamType.Point, Default = "0, 0", Unit = "px", Description = "Where the light is across the frame, in sequence pixels from the centre.")]
[Param("position-z", ParamType.Float, Default = "-800", Min = -1000000, Max = 1000000, SliderMax = 3000, Unit = "px", Label = "Position depth", Description = "The light's depth; negative is in front of the frame, towards the camera.")]
[Param("target", ParamType.Point, Default = "0, 0", Unit = "px", Description = "What a spot or directional light points at, across the frame.")]
[Param("target-z", ParamType.Float, Default = "0", Min = -1000000, Max = 1000000, SliderMax = 3000, Unit = "px", Label = "Target depth", Description = "The depth of what it points at.")]
[Param("cone-angle", ParamType.Float, Default = "90", Min = 1, Max = 179, Unit = "deg", Description = "How wide a spot light's cone is.")]
[Param("cone-feather", ParamType.Float, Default = "50", Min = 0, Max = 100, Unit = "%", Description = "How soft the edge of a spot light's cone is.")]
[Param("falloff", ParamType.Enum, Default = "none", Choices = "none, smooth, inverse-square", Animatable = false, Description = "How a point or spot light fades with distance: not at all, smoothly to nothing over the falloff distance, or as real light does.")]
[Param("radius", ParamType.Float, Default = "500", Min = 0, Max = 1000000, SliderMax = 3000, Unit = "px", Description = "How far a point or spot light reaches at full strength before it starts to fade.")]
[Param("falloff-distance", ParamType.Float, Default = "500", Min = 1, Max = 1000000, SliderMax = 3000, Unit = "px", Description = "How far past the radius a smooth falloff takes to reach nothing.")]
[Param("casts-shadows", ParamType.Bool, Default = "false", Animatable = false, Description = "Layers that cast shadows throw them from this light. Not for an ambient light.")]
[Param("shadow-darkness", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "How much of the light a shadow takes away, 0 to 1.")]
[Param("image", ParamType.Text, Default = "", Description = "For an environment light: the media item, by id, whose picture surrounds the scene; an equirectangular still (HDR or EXR is best).")]
[Param("shadow-softness", ParamType.Float, Default = "0", Min = 0, Max = 200, SliderMax = 50, Unit = "px", Description = "How soft a shadow's edge is.")]
public static class LightGenerator
{
}

/// <summary>
/// Extruded, bevelled text (Phase 48): any installed font or one in the project's fonts folder, as
/// geometry in the 3D scene, lit, shadowed and seen through the camera. Its clip is always 3D.
/// </summary>
[Generator(SceneObjects.Text, Name = "3D text", Category = "3D", Description = "Text with depth and a bevel, as solid geometry: lit, shadowed and seen through the camera.")]
[Param("text", ParamType.Text, Default = "JAZZ", Description = "What it says; \n for a new line.")]
[Param("font", ParamType.Text, Default = "Segoe UI", Description = "The font family, installed or in the project's fonts folder.")]
[Param("weight", ParamType.Enum, Default = "bold", Choices = "thin, light, regular, medium, semibold, bold, black", Animatable = false, Description = "How heavy the letters are.")]
[Param("italic", ParamType.Bool, Default = "false", Animatable = false, Description = "Slanted letters.")]
[Param("size", ParamType.Float, Default = "200", Min = 1, Max = 10000, SliderMax = 1000, Unit = "px", Description = "The height of the letters, in sequence pixels.")]
[Param("align", ParamType.Enum, Default = "centre", Choices = "left, centre, right", Animatable = false, Description = "How lines line up with each other.")]
[Param("depth", ParamType.Float, Default = "60", Min = 0, Max = 10000, SliderMax = 400, Unit = "px", Description = "How deep the letters are, front to back.")]
[Param("bevel", ParamType.Float, Default = "6", Min = 0, Max = 200, SliderMax = 40, Unit = "px", Description = "How wide the rounded edge round the front and back faces is.")]
[Param("bevel-segments", ParamType.Int, Default = "4", Min = 1, Max = 16, Animatable = false, Description = "Steps round the bevel; more is smoother.")]
[Param("color", ParamType.Color, Default = "#FFFFFF", Description = "The front and back faces and the bevel.")]
[Param("side-color", ParamType.Color, Default = "#9AA4B0", Description = "The sides.")]
[Param("metallic", ParamType.Float, Default = "0", Min = 0, Max = 1, Description = "0 painted, 1 metal.")]
[Param("roughness", ParamType.Float, Default = "0.35", Min = 0, Max = 1, Description = "0 polished, 1 matte.")]
public static class TextGenerator3D
{
}

/// <summary>A solid in the 3D scene (Phase 48): cube, sphere, cylinder, cone, torus or plane. Its clip is always 3D.</summary>
[Generator(SceneObjects.Shape, Name = "3D shape", Category = "3D", Description = "A solid: cube, sphere, cylinder, cone, torus or plane, lit and shadowed in the 3D scene.")]
[Param("kind", ParamType.Enum, Default = "cube", Choices = "cube, sphere, cylinder, cone, torus, plane", Animatable = false, Description = "What shape.")]
[Param("size", ParamType.Float2, Default = "300, 300", Unit = "px", Description = "Width and height, in sequence pixels.")]
[Param("depth", ParamType.Float, Default = "300", Min = 0, Max = 100000, SliderMax = 2000, Unit = "px", Description = "Front to back, in sequence pixels.")]
[Param("segments", ParamType.Int, Default = "48", Min = 3, Max = 512, Animatable = false, Description = "Steps round a curved shape; more is smoother.")]
[Param("thickness", ParamType.Float, Default = "0.3", Min = 0.01, Max = 1, Description = "A torus's tube, as a share of its radius.")]
[Param("color", ParamType.Color, Default = "#C8C8C8", Description = "Its colour.")]
[Param("metallic", ParamType.Float, Default = "0", Min = 0, Max = 1, Description = "0 painted, 1 metal.")]
[Param("roughness", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "0 polished, 1 matte.")]
public static class ShapeGenerator3D
{
}

/// <summary>
/// A glTF 2.0 model (Phase 48), <c>.gltf</c> or <c>.glb</c>, with its own materials, textures and
/// animations. Its clip is always 3D; the model is centred on the clip's position.
/// </summary>
[Generator(SceneObjects.Model, Name = "3D model", Category = "3D", Description = "A glTF model with its materials and animations, in the 3D scene.")]
[Param("file", ParamType.Text, Default = "", Description = "The .gltf or .glb file, relative to the project.")]
[Param("size", ParamType.Float, Default = "400", Min = 1, Max = 100000, SliderMax = 2000, Unit = "px", Description = "How big the model's largest side is, in sequence pixels.")]
[Param("animation", ParamType.Text, Default = "", Description = "Which of its animations plays, by name or number from 0; the first when empty.")]
[Param("animate", ParamType.Bool, Default = "true", Animatable = false, Description = "Play its animation over the clip.")]
[Param("speed", ParamType.Float, Default = "1", Min = 0, Max = 100, SliderMax = 4, Description = "How fast its animation plays.")]
[Param("loop", ParamType.Bool, Default = "true", Animatable = false, Description = "Start its animation again when it ends.")]
public static class ModelGenerator3D
{
}
