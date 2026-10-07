using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Transitions;

/// <summary>The outgoing picture fades into the incoming one.</summary>
[Transition("transition.crossfade", Name = "Crossfade", Category = "Dissolve", Description = "The outgoing picture fades into the incoming one: the cross dissolve every editor starts with.")]
[Param("style", ParamType.Enum, Default = "cross", Choices = "cross, film", Animatable = false, Description = "cross mixes as the eye sees, evenly; film mixes light, so the brights carry through the middle.")]
public sealed class CrossfadeTransition() : SinglePassTransition("PsCrossfade")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress) =>
        new() { Mode = Choice(parameters, "style") };
}

/// <summary>Fades out to a colour and in from it.</summary>
[Transition("transition.dip", Name = "Dip to colour", Category = "Dissolve", Description = "Fades the outgoing picture out to a colour, black by default, and the incoming one in from it.")]
[Param("colour", ParamType.Color, Default = "#000000", Description = "The colour it dips to.")]
[Param("hold", ParamType.Float, Default = "0", Min = 0, Max = 0.9, Description = "The share of the transition spent on the colour alone, 0 to 0.9.")]
public sealed class DipTransition() : SinglePassTransition("PsDip")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress) =>
        new() { Tint = Premultiplied(parameters.Color("colour")), Values = new Vector4(parameters.Float("hold"), 0, 0, 0) };

    internal static Vector4 Premultiplied(Vector4 colour) => new(colour.X * colour.W, colour.Y * colour.W, colour.Z * colour.W, colour.W);
}

/// <summary>A straight edge crosses the frame.</summary>
[Transition("transition.wipe.linear", Name = "Linear wipe", Category = "Wipe", Description = "A straight edge crosses the frame at any angle, uncovering the incoming picture behind it.")]
[Param("angle", ParamType.Float, Default = "0", Min = -360, Max = 360, Unit = "deg", Description = "The way the edge travels: 0 left to right, 90 top to bottom.")]
[Param("softness", ParamType.Float, Default = "0", Min = 0, Max = 1000, SliderMax = 200, Unit = "px", Description = "How soft the edge is, in sequence pixels.")]
[Param("border", ParamType.Float, Default = "0", Min = 0, Max = 200, SliderMax = 50, Unit = "px", Description = "A band of colour along the edge, in sequence pixels wide.")]
[Param("border-colour", ParamType.Color, Default = "#FFFFFF", Description = "The colour of the band along the edge.")]
public sealed class LinearWipeTransition() : SinglePassTransition("PsWipeLinear")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress) => new()
    {
        Values = new Vector4(parameters.Float("angle") * MathF.PI / 180.0f, parameters.Float("softness") * context.QualityScale, parameters.Float("border") * context.QualityScale, 0),
        Tint = DipTransition.Premultiplied(parameters.Color("border-colour")),
    };
}

/// <summary>A hand sweeps round the centre of the frame.</summary>
[Transition("transition.wipe.clock", Name = "Clock wipe", Category = "Wipe", Description = "A hand sweeps round the centre of the frame like a clock's, uncovering the incoming picture.")]
[Param("start-angle", ParamType.Float, Default = "0", Min = -360, Max = 360, Unit = "deg", Description = "Where the hand starts, clockwise from twelve o'clock.")]
[Param("direction", ParamType.Enum, Default = "clockwise", Choices = "clockwise, counterclockwise", Animatable = false, Description = "Which way the hand turns.")]
[Param("softness", ParamType.Float, Default = "0", Min = 0, Max = 1000, SliderMax = 200, Unit = "px", Description = "How soft the moving edge is, in sequence pixels.")]
[Param("border", ParamType.Float, Default = "0", Min = 0, Max = 200, SliderMax = 50, Unit = "px", Description = "A band of colour along the moving edge, in sequence pixels wide.")]
[Param("border-colour", ParamType.Color, Default = "#FFFFFF", Description = "The colour of the band along the edge.")]
public sealed class ClockWipeTransition() : SinglePassTransition("PsWipeClock")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress) => new()
    {
        Values = new Vector4(parameters.Float("start-angle") * MathF.PI / 180.0f, parameters.Float("softness") * context.QualityScale, parameters.Float("border") * context.QualityScale, 0),
        Flag = Choice(parameters, "direction"),
        Tint = DipTransition.Premultiplied(parameters.Color("border-colour")),
    };
}

/// <summary>An edge swings a quarter turn about a corner.</summary>
[Transition("transition.wipe.radial", Name = "Radial wipe", Category = "Wipe", Description = "An edge pivots on a corner of the frame and swings a quarter turn across it.")]
[Param("corner", ParamType.Enum, Default = "top-left", Choices = "top-left, top-right, bottom-left, bottom-right", Animatable = false, Description = "The corner the edge pivots on.")]
[Param("direction", ParamType.Enum, Default = "clockwise", Choices = "clockwise, counterclockwise", Animatable = false, Description = "Which way it swings: clockwise starts along the top or bottom edge, counterclockwise along the side.")]
[Param("softness", ParamType.Float, Default = "0", Min = 0, Max = 1000, SliderMax = 200, Unit = "px", Description = "How soft the edge is, in sequence pixels.")]
[Param("border", ParamType.Float, Default = "0", Min = 0, Max = 200, SliderMax = 50, Unit = "px", Description = "A band of colour along the edge, in sequence pixels wide.")]
[Param("border-colour", ParamType.Color, Default = "#FFFFFF", Description = "The colour of the band along the edge.")]
public sealed class RadialWipeTransition() : SinglePassTransition("PsWipeRadial")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress) => new()
    {
        Mode = Choice(parameters, "corner"),
        Values = new Vector4(0, parameters.Float("softness") * context.QualityScale, parameters.Float("border") * context.QualityScale, 0),
        Flag = Choice(parameters, "direction"),
        Tint = DipTransition.Premultiplied(parameters.Color("border-colour")),
    };
}

/// <summary>A shape opens from a point, or closes onto it.</summary>
[Transition("transition.wipe.iris", Name = "Iris", Category = "Wipe", Description = "A circle, diamond or box opens from a point to show the incoming picture, or closes onto it.")]
[Param("shape", ParamType.Enum, Default = "circle", Choices = "circle, diamond, box", Animatable = false, Description = "The shape that opens: a box has the frame's proportions.")]
[Param("centre", ParamType.Point, Default = "0, 0", Unit = "px", Description = "Where it opens from, in sequence pixels from the frame centre.")]
[Param("direction", ParamType.Enum, Default = "open", Choices = "open, close", Animatable = false, Description = "open grows the incoming picture from the centre; close shrinks the outgoing one into it.")]
[Param("softness", ParamType.Float, Default = "0", Min = 0, Max = 1000, SliderMax = 200, Unit = "px", Description = "How soft the edge is, in sequence pixels.")]
[Param("border", ParamType.Float, Default = "0", Min = 0, Max = 200, SliderMax = 50, Unit = "px", Description = "A band of colour along the edge, in sequence pixels wide.")]
[Param("border-colour", ParamType.Color, Default = "#FFFFFF", Description = "The colour of the band along the edge.")]
public sealed class IrisTransition() : SinglePassTransition("PsIris")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress)
    {
        Vector2 centre = CentreTexels(context, parameters.Float2("centre"));
        return new TransitionValues
        {
            Mode = Choice(parameters, "shape"),
            Values = new Vector4(centre.X, centre.Y, parameters.Float("softness") * context.QualityScale, parameters.Float("border") * context.QualityScale),
            Flag = Choice(parameters, "direction"),
            Tint = DipTransition.Premultiplied(parameters.Color("border-colour")),
        };
    }

    /// <summary>A point in sequence pixels from the frame centre, in texels from the top left of the working frame.</summary>
    internal static Vector2 CentreTexels(EffectContext context, Vector2 fromCentre)
    {
        Vector2 frame = new(context.FrameWidth, context.FrameHeight);
        return (frame / 2.0f) + (fromCentre * context.QualityScale);
    }
}

/// <summary>The incoming picture pushes the outgoing one off the frame.</summary>
[Transition("transition.push", Name = "Push", Category = "Slide", Description = "The incoming picture pushes the outgoing one off the frame.")]
[Param("direction", ParamType.Enum, Default = "left", Choices = "left, right, up, down", Animatable = false, Description = "The way the pictures move.")]
public sealed class PushTransition() : SinglePassTransition("PsPush")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress) =>
        new() { Mode = Choice(parameters, "direction") };
}

/// <summary>One picture slides over the other.</summary>
[Transition("transition.slide", Name = "Slide", Category = "Slide", Description = "The incoming picture slides in over the outgoing one, or the outgoing one slides away to uncover it.")]
[Param("direction", ParamType.Enum, Default = "left", Choices = "left, right, up, down", Animatable = false, Description = "The way the moving picture goes.")]
[Param("mode", ParamType.Enum, Default = "in", Choices = "in, out", Animatable = false, Description = "in slides the incoming picture over; out slides the outgoing one away.")]
public sealed class SlideTransition() : SinglePassTransition("PsSlide")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress) =>
        new() { Mode = Choice(parameters, "direction"), Flag = Choice(parameters, "mode") };
}

/// <summary>Zooms into the outgoing picture and out of the incoming one.</summary>
[Transition("transition.zoom", Name = "Zoom", Category = "Motion", Description = "Zooms into the outgoing picture and back out of the incoming one, crossing over in the middle.")]
[Param("amount", ParamType.Float, Default = "3", Min = 1, Max = 10, Description = "How far it zooms at the cross over: 2 is twice as close.")]
[Param("centre", ParamType.Point, Default = "0, 0", Unit = "px", Description = "What it zooms towards, in sequence pixels from the frame centre.")]
public sealed class ZoomTransition() : SinglePassTransition("PsZoom")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress)
    {
        Vector2 centre = IrisTransition.CentreTexels(context, parameters.Float2("centre"));
        return new TransitionValues
        {
            Values = new Vector4(parameters.Float("amount"), centre.X / Math.Max(1, context.FrameWidth), centre.Y / Math.Max(1, context.FrameHeight), 0),
        };
    }
}

/// <summary>Both pictures blur as one dissolves into the other.</summary>
[Transition("transition.blur-dissolve", Name = "Blur dissolve", Category = "Dissolve", Description = "Both pictures blur as they cross, sharpest at the ends and softest in the middle.")]
[Param("radius", ParamType.Float, Default = "40", Min = 0, Max = 400, SliderMax = 150, Unit = "px", Description = "How far the blur spreads in the middle, in sequence pixels.")]
public sealed class BlurDissolveTransition() : SinglePassTransition("PsBlurDissolve")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress) =>
        new() { Values = new Vector4(parameters.Float("radius") * context.QualityScale, 0, 0, 0) };
}

/// <summary>A digital glitch: torn rows, split channels, blocks turning over.</summary>
[Transition("transition.glitch", Name = "Glitch", Category = "Stylize", Description = "Rows tear sideways, the colour channels split and the picture turns over block by block, like a bad signal.")]
[Param("intensity", ParamType.Float, Default = "0.6", Min = 0, Max = 1, Description = "How violent the tearing and splitting is.")]
[Param("block-size", ParamType.Float, Default = "32", Min = 2, Max = 400, SliderMax = 120, Unit = "px", Description = "The size of the blocks that turn over, in sequence pixels.")]
[Param("seed", ParamType.Int, Default = "0", Min = 0, Max = 9999, Animatable = false, Description = "Another number for a different pattern of glitches.")]
public sealed class GlitchTransition() : SinglePassTransition("PsGlitch")
{
    /// <inheritdoc />
    protected override TransitionValues Values(EffectContext context, ParameterSet parameters, float progress) =>
        new() { Values = new Vector4(parameters.Float("intensity"), parameters.Float("block-size") * context.QualityScale, parameters.Int("seed"), 0) };
}
