using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Render.Color;
using JazzHands.Render.Frames;

namespace JazzHands.Render.Compositing;

/// <summary>What a layer's picture comes from.</summary>
public abstract record LayerSource;

/// <summary>A decoded frame.</summary>
/// <param name="Frame">The frame, owned by whoever supplied it; valid for the duration of the render.</param>
/// <param name="Color">How to turn its samples into light.</param>
/// <param name="Identity">What the frame is (media, stream, time), for the layer cache.</param>
public sealed record FrameLayerSource(FrameTexture Frame, YuvColorSpace Color, string Identity) : LayerSource;

/// <summary>A flat colour, premultiplied linear light.</summary>
public sealed record SolidLayerSource(Vector4 Color) : LayerSource;

/// <summary>
/// A generator's picture, drawn at the working resolution each frame: a gradient, noise, a shape,
/// a countdown. <paramref name="Node"/> carries its type, its parameters at this frame and its
/// times, as an effect's node does.
/// </summary>
public sealed record GeneratorLayerSource(EffectNode Node) : LayerSource;

/// <summary>Another sequence, rendered first and then placed like any other picture.</summary>
public sealed record NestedLayerSource(RenderGraph Graph) : LayerSource;

/// <summary>
/// A track inside a transition: the outgoing and the incoming clip, each placed, effected and
/// masked as a layer of its own over nothing, then mixed by the transition into one frame-sized
/// picture, which is laid over the stack. Either may be null where a clip has no picture.
/// </summary>
/// <param name="Outgoing">The clip the transition leaves.</param>
/// <param name="Incoming">The clip it arrives at.</param>
/// <param name="Transition">Which transition, its parameters and how far through it the frame is.</param>
public sealed record TransitionLayerSource(LayerNode? Outgoing, LayerNode? Incoming, TransitionNode Transition) : LayerSource;

/// <summary>
/// A layer with motion blur: the same layer placed at moments across the shutter, each drawn over
/// nothing, averaged into one frame-sized picture in linear light. An echo is the same with the
/// layer at earlier frames and weights that fade with age.
/// </summary>
/// <param name="Samples">The layer at each moment, each with its own place, opacity, masks and effects.</param>
/// <param name="Weights">Each sample's share, summing to one; equal shares when not given.</param>
public sealed record MotionBlurLayerSource(ImmutableArray<LayerNode> Samples, ImmutableArray<float> Weights = default) : LayerSource;

/// <summary>A track matte at one moment: the matte track drawn alone, and what of it the layer keeps.</summary>
/// <param name="Graph">The matte track's layers at this moment, at the frame's size.</param>
/// <param name="Mode">Alpha, luma, or either inverted.</param>
public sealed record TrackMatteNode(RenderGraph Graph, TrackMatteMode Mode);

/// <summary>A transition at one frame, with its parameters evaluated and its progress eased.</summary>
/// <param name="Effect">The transition's type and parameters, as an effect node: its time is from the start of its span, its owner length the span's.</param>
/// <param name="Progress">How far through it the frame is, 0 to 1, after easing.</param>
public sealed record TransitionNode(EffectNode Effect, float Progress)
{
    /// <summary>The progress before easing, which is how far through the span in time the frame is.</summary>
    public float Linear { get; init; } = Progress;

    /// <summary>A runner in place of the registered one, for a test's own transition.</summary>
    public ITransitionRunner? Custom { get; init; }
}

/// <summary>A transition that is not a registered type: a test's, or one built in code.</summary>
public interface ITransitionRunner
{
    /// <summary>Mixes <paramref name="outgoing"/> and <paramref name="incoming"/> at a progress into <paramref name="output"/>, all the same size.</summary>
    void Apply(EffectContext context, float progress, RenderTarget outgoing, RenderTarget incoming, RenderTarget output);
}

/// <summary>One mask on a layer, resolved to numbers at the time being rendered.</summary>
/// <param name="Shape">Rectangle, ellipse, polygon or bezier path.</param>
/// <param name="Bounds">x, y, width, height of a rectangle or ellipse, in source pixels.</param>
/// <param name="PathData">The outline of a polygon or path, in source pixels, SVG path syntax.</param>
/// <param name="Feather">Softening, in sequence pixels.</param>
/// <param name="Opacity">How strongly it applies, 0 to 1.</param>
/// <param name="Mode">How it combines with the masks before it.</param>
/// <param name="Invert">Keep the outside instead of the inside.</param>
/// <param name="Expansion">Grows the shape by this many sequence pixels; shrinks it when negative.</param>
public sealed record MatteShape(
    MaskShape Shape,
    Vector4 Bounds,
    string PathData,
    float Feather,
    float Opacity,
    MaskMode Mode,
    bool Invert,
    float Expansion = 0.0f);

/// <summary>One layer of the stack, with every animated value already evaluated.</summary>
/// <param name="Source">Where the picture comes from.</param>
/// <param name="SourceWidth">The picture's width in its own pixels.</param>
/// <param name="SourceHeight">Its height.</param>
/// <param name="Transform">Source pixels to target pixels: fit, anchor, scale, rotation, position and quality.</param>
/// <param name="Crop">The part of the source kept, left, top, right, bottom as texture coordinates.</param>
/// <param name="Opacity">0 to 1.</param>
/// <param name="Blend">How it combines with what is underneath.</param>
/// <param name="Masks">Masks in source pixels, drawn through <paramref name="Transform"/>.</param>
/// <param name="Scale">Target pixels per sequence pixel: the quality. Feathers and pixel-sized effect parameters are multiplied by it.</param>
public sealed record LayerNode(
    LayerSource Source,
    int SourceWidth,
    int SourceHeight,
    Matrix3x2 Transform,
    Vector4 Crop,
    float Opacity,
    BlendMode Blend,
    ImmutableArray<MatteShape> Masks,
    float Scale = 1.0f)
{
    /// <summary>
    /// The effects, first to last: the clip's, then its track's. A picture layer runs them over
    /// its placed picture, in frame space, before masks and blending. An adjustment layer runs
    /// them over everything underneath and composites the result back over it with its opacity,
    /// blend and masks.
    /// </summary>
    public ImmutableArray<EffectNode> Effects { get; init; } = [];

    /// <summary>Another track's picture this layer shows only through, and how; null for none.</summary>
    public TrackMatteNode? TrackMatte { get; init; }

    /// <summary>True for an adjustment layer, which has no picture of its own.</summary>
    public bool IsAdjustment { get; init; }

    /// <summary>The full source, uncropped.</summary>
    public static Vector4 NoCrop { get; } = new(0.0f, 0.0f, 1.0f, 1.0f);
}

/// <summary>
/// Everything needed to draw one frame of a sequence, bottom layer first.
/// </summary>
/// <param name="Width">The output width in pixels: the sequence width times the quality.</param>
/// <param name="Height">The output height.</param>
/// <param name="Layers">The stack, bottom first.</param>
public sealed record RenderGraph(int Width, int Height, ImmutableArray<LayerNode> Layers)
{
    /// <summary>The folder the project file is in, for effects that read files named relative to it (a LUT). Empty for none.</summary>
    public string ProjectFolder { get; init; } = string.Empty;

    /// <summary>Sample transformed layers with a bicubic filter rather than a bilinear one.</summary>
    public bool Bicubic { get; init; } = true;

    /// <summary>
    /// Keep transformed layers between frames, so scrubbing over a still region redraws nothing.
    /// Off while playing, where every frame is new and a cache would only hold memory.
    /// </summary>
    public bool CacheLayers { get; init; }

    /// <summary>An empty frame.</summary>
    public static RenderGraph Empty(int width, int height) => new(width, height, []);
}

/// <summary>
/// An effect that turns one picture into another without being a registered type: a test's, or
/// one built in code. Registered effects are <see cref="Effects.VideoEffect"/>s.
/// </summary>
public interface ILayerEffect
{
    /// <summary>Reads <paramref name="input"/> and writes the result into <paramref name="output"/>, the same size.</summary>
    void Apply(EffectContext context, RenderTarget input, RenderTarget output);
}

/// <summary>
/// One effect in a layer's chain, with its parameters already evaluated for the frame.
/// </summary>
/// <param name="Descriptor">Which effect, and the class the compositor runs for it.</param>
/// <param name="Parameters">Its parameters at this frame.</param>
public sealed record EffectNode(EffectDescriptor Descriptor, ParameterSet Parameters)
{
    private static readonly EffectDescriptor CustomDescriptor = new("custom", EffectKind.Video, "Custom", "Custom", string.Empty, []);

    /// <summary>The effect instance's identifier.</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>The frame's time relative to the effect's owner.</summary>
    public Flicks LocalTime { get; init; }

    /// <summary>A seed stable for the instance, from its identifier.</summary>
    public int Seed { get; init; }

    /// <summary>The instance as the project holds it, for an effect that evaluates itself at other times.</summary>
    public Effect? Model { get; init; }

    /// <summary>Masks limiting where the effect applies, in the layer's source pixels; everywhere when empty.</summary>
    public ImmutableArray<MatteShape> Masks { get; init; } = [];

    /// <summary>How long the effect's owner lasts, for effects that run across it (a Ken Burns move).</summary>
    public Flicks OwnerLength { get; init; }

    /// <summary>The frame's time on the sequence, for effects driven by it (a timecode).</summary>
    public Flicks SequenceTime { get; init; }

    /// <summary>The sequence's frame rate.</summary>
    public Rational FrameRate { get; init; } = Rational.Fps30;

    /// <summary>A runner in place of the descriptor's class, for effects that are not registered.</summary>
    public ILayerEffect? Custom { get; init; }

    /// <summary>Wraps an unregistered effect.</summary>
    public static EffectNode From(ILayerEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return new EffectNode(CustomDescriptor, ParameterSet.Defaults(CustomDescriptor)) { Custom = effect };
    }
}
