using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Distort;

/// <summary>Reflects one half of the frame onto the other, or a quarter onto all four.</summary>
[VideoEffect("video.mirror", Name = "Mirror", Category = "Distort", Description = "Reflects one half of the picture onto the other half, or the top left quarter into all four.")]
[Param("side", ParamType.Enum, Default = "left-onto-right", Choices = "left-onto-right, right-onto-left, top-onto-bottom, bottom-onto-top, quarters", Animatable = false, Description = "Which half is kept and reflected.")]
[Param("offset", ParamType.Point, Default = "0, 0", Unit = "px", Description = "Moves the mirror line away from the centre, in sequence pixels.")]
public sealed class MirrorEffect() : SinglePassEffect("Distort.hlsl", "PsMirror")
{
    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        Vector2 offset = parameters.Float2("offset") * context.QualityScale;
        return new() { Values = new Vector4(offset, 0, 0), FlagX = Choice(parameters, "side") };
    }
}

/// <summary>Repeats the picture in a grid.</summary>
[VideoEffect("video.tile", Name = "Tile", Category = "Distort", Description = "Repeats the whole picture in a grid of smaller copies.")]
[Param("columns", ParamType.Int, Default = "2", Min = 1, Max = 32, SliderMax = 8, Description = "Copies across.")]
[Param("rows", ParamType.Int, Default = "2", Min = 1, Max = 32, SliderMax = 8, Description = "Copies down.")]
[Param("mirror", ParamType.Bool, Default = "false", Animatable = false, Description = "Flip every other copy so the edges meet seamlessly.")]
public sealed class TileEffect() : SinglePassEffect("Distort.hlsl", "PsTile")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) =>
        parameters.Int("columns") <= 1 && parameters.Int("rows") <= 1;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input) =>
        new() { Values = new Vector4(parameters.Int("columns"), parameters.Int("rows"), 0, 0), FlagX = Flag(parameters.Bool("mirror")) };
}

/// <summary>Barrel or pincushion distortion, as a wide or a long lens gives.</summary>
[VideoEffect("video.lens-distortion", Name = "Lens distortion", Category = "Distort", Description = "Bulges the picture like a wide angle lens (positive) or pinches it like a long one (negative); zoom hides the edges.")]
[Param("amount", ParamType.Float, Default = "0.2", Min = -1, Max = 1, Description = "Positive bulges out (barrel), negative pinches in (pincushion).")]
[Param("zoom", ParamType.Float, Default = "1", Min = 0.25, Max = 4, SliderMax = 2, Description = "Scales the result, to fill the corners a bulge pulls in.")]
[Param("centre", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The centre of the lens, in sequence pixels from the frame centre.")]
[Param("repeat-edges", ParamType.Bool, Default = "false", Animatable = false, Description = "Stretch the edge pixels where the picture runs out, instead of leaving it transparent.")]
public sealed class LensDistortionEffect() : SinglePassEffect("Distort.hlsl", "PsLens")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) =>
        parameters.Float("amount") == 0.0f && parameters.Float("zoom") == 1.0f;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        Vector2 centre = Uv(parameters.Float2("centre"), context, input);
        return new()
        {
            Values = new Vector4(parameters.Float("amount"), parameters.Float("zoom"), centre.X, centre.Y),
            FlagX = Flag(parameters.Bool("repeat-edges")),
        };
    }

    /// <summary>A point in sequence pixels from the frame centre as a texture coordinate of a target.</summary>
    internal static Vector2 Uv(Vector2 fromCentre, EffectContext context, RenderTarget target) =>
        new Vector2(0.5f) + (fromCentre * context.QualityScale / new Vector2(target.Width, target.Height));
}

/// <summary>A slow push in, pull out or pan across the clip: the Ken Burns move.</summary>
[VideoEffect("video.ken-burns", Name = "Ken Burns", Category = "Transform", Description = "Moves slowly across the picture from a start framing to an end framing over the clip's length, the documentary pan and zoom.")]
[Param("start-scale", ParamType.Float, Default = "1", Min = 0.1, Max = 20, SliderMax = 4, Description = "The zoom at the clip's start; 1 is the whole picture.")]
[Param("end-scale", ParamType.Float, Default = "1.25", Min = 0.1, Max = 20, SliderMax = 4, Description = "The zoom at the clip's end.")]
[Param("start-centre", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The point of the picture in the middle of the frame at the start, in sequence pixels from the centre.")]
[Param("end-centre", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The point in the middle of the frame at the end.")]
[Param("easing", ParamType.Enum, Default = "ease-in-out", Choices = "linear, ease-in, ease-out, ease-in-out", Animatable = false, Description = "How the move starts and stops.")]
public sealed class KenBurnsEffect() : SinglePassEffect("Distort.hlsl", "PsKenBurns")
{
    /// <summary>How far through the move a clip progress is, after easing.</summary>
    public static float Ease(string easing, float progress) => easing switch
    {
        "linear" => progress,
        "ease-in" => progress * progress,
        "ease-out" => 1.0f - ((1.0f - progress) * (1.0f - progress)),
        _ => progress * progress * (3.0f - (2.0f * progress)),
    };

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        float t = Ease(parameters.Enum("easing"), context.Progress);
        float scale = Lerp(parameters.Float("start-scale"), parameters.Float("end-scale"), t);
        Vector2 centre = Vector2.Lerp(parameters.Float2("start-centre"), parameters.Float2("end-centre"), t);
        Vector2 uv = LensDistortionEffect.Uv(centre, context, input);
        return new() { Values = new Vector4(scale, 0, uv.X, uv.Y) };
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}

/// <summary>Cuts away the edges of the picture as it stands in the frame, with a soft edge if wanted.</summary>
[VideoEffect("video.crop", Name = "Crop", Category = "Transform", Description = "Cuts away the edges of the picture where it now sits in the frame, with an optional soft edge.")]
[Param("left", ParamType.Float, Default = "0", Min = 0, Max = 100, Unit = "%", Description = "Percent of the frame's width cut from the left.")]
[Param("top", ParamType.Float, Default = "0", Min = 0, Max = 100, Unit = "%", Description = "Percent of the frame's height cut from the top.")]
[Param("right", ParamType.Float, Default = "0", Min = 0, Max = 100, Unit = "%", Description = "Percent of the frame's width cut from the right.")]
[Param("bottom", ParamType.Float, Default = "0", Min = 0, Max = 100, Unit = "%", Description = "Percent of the frame's height cut from the bottom.")]
[Param("feather", ParamType.Float, Default = "0", Min = 0, Max = 1000, SliderMax = 200, Unit = "px", Description = "Softens the cut edges inwards, in sequence pixels.")]
public sealed class CropEffect() : SinglePassEffect("Distort.hlsl", "PsCrop")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) =>
        parameters.Float("left") + parameters.Float("top") + parameters.Float("right") + parameters.Float("bottom") + parameters.Float("feather") == 0.0f;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input) =>
        new()
        {
            Values = new Vector4(
                parameters.Float("left") / 100.0f,
                parameters.Float("top") / 100.0f,
                1.0f - (parameters.Float("right") / 100.0f),
                1.0f - (parameters.Float("bottom") / 100.0f)),
            More = new Vector4(parameters.Float("feather") * context.QualityScale, 0, 0, 0),
        };
}
