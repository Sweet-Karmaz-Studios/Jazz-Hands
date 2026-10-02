using System.Collections.Immutable;
using JazzHands.Core.Model;

namespace JazzHands.Core.Effects;

/// <summary>
/// What the comp graph's own node types are and the parameters they take (Phase 49). Registered
/// video effects and generators are nodes too, with their own descriptors; these are the ones only
/// a graph has, so they are not offered in the Effects panel.
/// </summary>
public static class CompNodes
{
    private const string Category = "Nodes";

    /// <summary>
    /// Where a 3D object sits in a render: text, a shape or a model wired into a 3D render takes
    /// its own parameters and these.
    /// </summary>
    public static EffectDescriptor Placement3D { get; } = new(
        "comp.placement3d",
        EffectKind.Video,
        "3D place",
        Category,
        "Where it sits in the 3D render.",
        [
            new ParamDescriptor("position", ParamType.Point, new ParamValue.Float2(0, 0), "Position", "Across the frame from its centre, in sequence pixels.", Unit: "px"),
            new ParamDescriptor("z", ParamType.Float, new ParamValue.Float(0), "Depth", "Away from the viewer, in sequence pixels.", Min: -1000000, Max: 1000000, SliderMax: 3000, Unit: "px"),
            new ParamDescriptor("rotation-x", ParamType.Float, new ParamValue.Float(0), "X rotation", "Degrees; positive tips the top edge away.", Min: -36000, Max: 36000, SliderMax: 360, Unit: "deg"),
            new ParamDescriptor("rotation-y", ParamType.Float, new ParamValue.Float(0), "Y rotation", "Degrees; positive turns the right edge away.", Min: -36000, Max: 36000, SliderMax: 360, Unit: "deg"),
            new ParamDescriptor("rotation", ParamType.Float, new ParamValue.Float(0), "Rotation", "Degrees clockwise.", Min: -36000, Max: 36000, SliderMax: 360, Unit: "deg"),
            new ParamDescriptor("scale", ParamType.Float2, new ParamValue.Float2(1, 1), "Scale", "Multiplier per axis."),
        ]);

    /// <summary>The picture coming into the graph.</summary>
    public static EffectDescriptor In { get; } = Node(CompGraph.In, "In", "The clip's own picture, placed, coming into the graph.");

    /// <summary>What the graph shows.</summary>
    public static EffectDescriptor Out { get; } = Node(CompGraph.Out, "Out", "What the graph shows: the clip's picture from here on.");

    /// <summary>Another media item's picture.</summary>
    public static EffectDescriptor Media { get; } = Node(
        CompGraph.Media,
        "Media",
        "Another media item's picture, fitted to the frame.",
        new ParamDescriptor("media", ParamType.Text, new ParamValue.Text(string.Empty), "Media", "The media item, by id."),
        new ParamDescriptor("offset", ParamType.Float, new ParamValue.Float(0), "Offset", "Seconds into the media at the clip's start.", Min: -36000, Max: 36000, SliderMax: 60, Unit: "s"),
        new ParamDescriptor("fit", ParamType.Enum, new ParamValue.Enum("fit"), "Fit", "Fit inside the frame, fill it, stretch to it, or at its own size.", Animatable: false, Choices: new EquatableArray<string>(["fit", "fill", "stretch", "native"])));

    /// <summary>A foreground over a background.</summary>
    public static EffectDescriptor Merge { get; } = Node(
        CompGraph.Merge,
        "Merge",
        "The foreground over the background: blended, faded, placed, and kept only where a mask's alpha says.",
        [
            new ParamDescriptor("blend", ParamType.Enum, new ParamValue.Enum("normal"), "Blend", "How the foreground combines with the background.", Animatable: false, Choices: new EquatableArray<string>(["normal", "add", "multiply", "screen", "overlay", "darken", "lighten", "difference", "soft-light", "hard-light"])),
            new ParamDescriptor("opacity", ParamType.Float, new ParamValue.Float(1), "Opacity", "How much of the foreground shows, 0 to 1.", Min: 0, Max: 1),
            .. Placement,
        ]);

    /// <summary>A picture moved, scaled and turned.</summary>
    public static EffectDescriptor Transform { get; } = Node(CompGraph.Transform, "Transform", "Its picture moved, scaled and turned in the frame.", [.. Placement]);

    /// <summary>A picture kept where another says.</summary>
    public static EffectDescriptor Matte { get; } = Node(
        CompGraph.Matte,
        "Matte",
        "Its picture kept only where the matte's alpha, or brightness, says.",
        new ParamDescriptor("mode", ParamType.Enum, new ParamValue.Enum("alpha"), "Mode", "Keep where the matte is opaque or bright, or where it is not.", Animatable: false, Choices: new EquatableArray<string>(["alpha", "luma", "alpha-inverted", "luma-inverted"])));

    /// <summary>A picture as a plane in 3D.</summary>
    public static EffectDescriptor Plane { get; } = Node(
        CompGraph.Plane,
        "3D plane",
        "Its picture as a plane in a 3D render, placed in depth and turned.",
        [
            .. Placement3D.Params,
            new ParamDescriptor("lights", ParamType.Bool, new ParamValue.Bool(true), "Takes lights", "Lit by the render's lights.", Animatable: false),
            new ParamDescriptor("casts-shadows", ParamType.Bool, new ParamValue.Bool(true), "Casts shadows", "Throws shadows from lights that cast them.", Animatable: false),
            new ParamDescriptor("accepts-shadows", ParamType.Bool, new ParamValue.Bool(true), "Takes shadows", "Darkened by shadows.", Animatable: false),
        ]);

    /// <summary>A 3D scene of what is wired into it.</summary>
    public static EffectDescriptor Render3D { get; } = Node(CompGraph.Render3D, "3D render", "A 3D scene of the planes, text, shapes, models, camera and lights wired into it, seen through the camera (the default one when none is).");

    /// <summary>Every node type a graph has of its own.</summary>
    public static ImmutableArray<EffectDescriptor> All { get; } = [In, Out, Media, Merge, Transform, Matte, Plane, Render3D];

    /// <summary>Short names a person types for node types: <c>merge</c> for <c>comp.merge</c>, <c>text3d</c> for <c>3d.text</c>.</summary>
    public static IReadOnlyDictionary<string, string> ShortNames { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["in"] = CompGraph.In,
        ["out"] = CompGraph.Out,
        ["media"] = CompGraph.Media,
        ["merge"] = CompGraph.Merge,
        ["transform"] = CompGraph.Transform,
        ["matte"] = CompGraph.Matte,
        ["plane3d"] = CompGraph.Plane,
        ["render3d"] = CompGraph.Render3D,
        ["text3d"] = SceneObjects.Text,
        ["shape3d"] = SceneObjects.Shape,
        ["model3d"] = SceneObjects.Model,
        ["camera3d"] = SceneObjects.Camera,
        ["light3d"] = SceneObjects.Light,
    };

    /// <summary>A graph node type's own descriptor, or null for a registered effect or generator.</summary>
    public static EffectDescriptor? Find(string typeId) => All.FirstOrDefault(descriptor => descriptor.TypeId == typeId);

    /// <summary>The move, scale and turn a merge and a transform share.</summary>
    private static ParamDescriptor[] Placement =>
    [
        new ParamDescriptor("position", ParamType.Point, new ParamValue.Float2(0, 0), "Position", "Offset from the frame centre, in sequence pixels.", Unit: "px"),
        new ParamDescriptor("scale", ParamType.Float2, new ParamValue.Float2(1, 1), "Scale", "Multiplier per axis; a negative flips.", SliderMax: 4),
        new ParamDescriptor("rotation", ParamType.Float, new ParamValue.Float(0), "Rotation", "Degrees clockwise.", Min: -36000, Max: 36000, SliderMax: 360, Unit: "deg"),
        new ParamDescriptor("anchor", ParamType.Float2, new ParamValue.Float2(0, 0), "Anchor", "The pivot, in pixels from the frame centre.", Unit: "px"),
    ];

    private static EffectDescriptor Node(string typeId, string name, string description, params ParamDescriptor[] parameters) =>
        new(typeId, EffectKind.Video, name, Category, description, [.. parameters]);
}
