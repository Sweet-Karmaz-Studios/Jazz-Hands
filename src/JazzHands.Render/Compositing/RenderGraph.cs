using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Model;
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

/// <summary>Another sequence, rendered first and then placed like any other picture.</summary>
public sealed record NestedLayerSource(RenderGraph Graph) : LayerSource;

/// <summary>One mask on a layer, resolved to numbers at the time being rendered.</summary>
/// <param name="Shape">Rectangle, ellipse, polygon or bezier path.</param>
/// <param name="Bounds">x, y, width, height of a rectangle or ellipse, in source pixels.</param>
/// <param name="PathData">The outline of a polygon or path, in source pixels, SVG path syntax.</param>
/// <param name="Feather">Softening, in sequence pixels.</param>
/// <param name="Opacity">How strongly it applies, 0 to 1.</param>
/// <param name="Mode">How it combines with the masks before it.</param>
/// <param name="Invert">Keep the outside instead of the inside.</param>
public sealed record MatteShape(
    MaskShape Shape,
    Vector4 Bounds,
    string PathData,
    float Feather,
    float Opacity,
    MaskMode Mode,
    bool Invert);

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
    /// For an adjustment layer: the effects applied to everything underneath, whose result is
    /// then composited back over it with this layer's opacity, blend and masks. Empty otherwise.
    /// </summary>
    public ImmutableArray<ILayerEffect> Effects { get; init; } = [];

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

/// <summary>What an effect is given to work with.</summary>
/// <param name="Device">The device.</param>
/// <param name="Pool">Where intermediate targets come from.</param>
/// <param name="QualityScale">Output pixels per sequence pixel, which pixel-sized parameters are multiplied by.</param>
public sealed record EffectContext(RenderDevice Device, RenderTargetPool Pool, float QualityScale);

/// <summary>
/// An effect that turns one picture into another. Phase 15 builds the registry and the real
/// effects on this; Phase 10 uses it for adjustment layers.
/// </summary>
public interface ILayerEffect
{
    /// <summary>Reads <paramref name="input"/> and writes the result into <paramref name="output"/>, the same size.</summary>
    void Apply(EffectContext context, RenderTarget input, RenderTarget output);
}
