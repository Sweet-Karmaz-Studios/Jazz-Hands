using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Impact;

/// <summary>Camera shake: the picture knocked about by smooth seeded noise, dying away from a trigger.</summary>
[VideoEffect("video.shake", Name = "Shake", Category = "Impact", Seeded = true, Description = "Shakes the picture as a hit or an explosion shakes a camera: seeded, smooth noise at a frequency, strongest at the trigger and dying away at the decay rate. On an adjustment layer it shakes everything below.")]
[Param("amplitude", ParamType.Float, Default = "24", Min = 0, Max = 2000, SliderMax = 200, Unit = "px", Description = "How far it moves at its strongest, in sequence pixels.")]
[Param("frequency", ParamType.Float, Default = "12", Min = 0.1, Max = 120, SliderMax = 40, Unit = "Hz", Description = "How fast it shakes; 5 is a slow sway, 20 a hard rattle.")]
[Param("rotation", ParamType.Float, Default = "1.5", Min = 0, Max = 45, SliderMax = 10, Unit = "deg", Description = "How far it turns at its strongest.")]
[Param("decay", ParamType.Float, Default = "4", Min = 0, Max = 100, SliderMax = 20, Description = "How fast it dies away, per second; 0 keeps shaking at full strength.")]
[Param("trigger", ParamType.Float, Default = "0", Min = 0, Max = 86400, SliderMax = 10, Unit = "s", Animatable = false, Description = "When it starts, in seconds from the start of what it is on. vfx.apply-preset puts it on the hit.")]
[Param("edges", ParamType.Enum, Default = "mirror", Choices = "transparent, repeat, mirror", Animatable = false, Description = "What shows where the picture moves away from the frame's edge.")]
[Param("seed", ParamType.Int, Default = "1", Min = 0, Max = 100000, Animatable = false, Description = "Change it for a different shake with the same settings.")]
public sealed class ShakeEffect() : SinglePassEffect("Impact.hlsl", "PsMove")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) =>
        Move(context, parameters) is { Offset: var offset, Degrees: var degrees } && offset == Vector2.Zero && degrees == 0;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        (Vector2 offset, float degrees) = Move(context, parameters);
        return new()
        {
            Values = new Vector4(offset * context.QualityScale, degrees * MathF.PI / 180, 1),
            More = new Vector4(input.Width / 2.0f, input.Height / 2.0f, 0, 0),
            FlagX = Choice(parameters, "edges"),
        };
    }

    private static (Vector2 Offset, float Degrees) Move(EffectContext context, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        return ImpactCurves.Shake(
            context.Time.ToSeconds(),
            parameters.Float("trigger"),
            parameters.Float("frequency"),
            parameters.Float("amplitude"),
            parameters.Float("rotation"),
            parameters.Float("decay"),
            unchecked((uint)context.Seed + ((uint)parameters.Int("seed") * 7919u)));
    }
}

/// <summary>A fast zoom in on a hit, then a settle back.</summary>
[VideoEffect("video.zoom-punch", Name = "Zoom punch", Category = "Impact", Description = "Punches in on the picture at the trigger, fast, then settles back: the zoom on a hit, a reveal or a beat.")]
[Param("amount", ParamType.Float, Default = "1.12", Min = 0.1, Max = 10, SliderMax = 2, Description = "The scale at the peak; 1.12 is 12% bigger, under 1 punches out.")]
[Param("attack", ParamType.Float, Default = "0.05", Min = 0.001, Max = 10, SliderMax = 1, Unit = "s", Description = "How long it takes to reach the peak.")]
[Param("settle", ParamType.Float, Default = "0.35", Min = 0, Max = 30, SliderMax = 3, Unit = "s", Description = "How long it takes to come back.")]
[Param("curve", ParamType.Enum, Default = "smooth", Choices = "smooth, back, elastic", Animatable = false, Description = "How it comes back: easing to rest, going a little past it, or wobbling like a spring.")]
[Param("trigger", ParamType.Float, Default = "0", Min = 0, Max = 86400, SliderMax = 10, Unit = "s", Animatable = false, Description = "When it punches, in seconds from the start of what it is on.")]
[Param("centre", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The point it zooms towards, in sequence pixels from the frame centre.")]
[Param("edges", ParamType.Enum, Default = "mirror", Choices = "transparent, repeat, mirror", Animatable = false, Description = "What shows at the edges when it punches out.")]
public sealed class ZoomPunchEffect() : SinglePassEffect("Impact.hlsl", "PsMove")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => Scale(context, parameters) == 1.0;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        Vector2 centre = (new Vector2(input.Width, input.Height) / 2) + (parameters.Float2("centre") * context.QualityScale);
        return new()
        {
            Values = new Vector4(0, 0, 0, (float)Scale(context, parameters)),
            More = new Vector4(centre, 0, 0),
            FlagX = Choice(parameters, "edges"),
        };
    }

    private static double Scale(EffectContext context, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        return ImpactCurves.Punch(
            context.Time.ToSeconds(),
            parameters.Float("trigger"),
            parameters.Float("amount"),
            parameters.Float("attack"),
            parameters.Float("settle"),
            (Settle)Choice(parameters, "curve"));
    }
}

/// <summary>A frame flashing white, or a colour, and fading.</summary>
[VideoEffect("video.flash", Name = "Flash", Category = "Impact", Description = "Flashes the picture to white or a colour at the trigger and fades back, fast at first: a hit, a gunshot, a cut on the beat.")]
[Param("colour", ParamType.Color, Default = "#FFFFFF", Description = "The flash's colour.")]
[Param("strength", ParamType.Float, Default = "0.85", Min = 0, Max = 1, Description = "How far towards the colour it goes at its peak.")]
[Param("attack", ParamType.Float, Default = "0", Min = 0, Max = 10, SliderMax = 1, Unit = "s", Description = "How long it takes to reach the peak; 0 is instant.")]
[Param("hold", ParamType.Float, Default = "0.03", Min = 0, Max = 10, SliderMax = 1, Unit = "s", Description = "How long it stays at the peak.")]
[Param("decay", ParamType.Float, Default = "0.25", Min = 0, Max = 30, SliderMax = 3, Unit = "s", Description = "How long it takes to fade.")]
[Param("trigger", ParamType.Float, Default = "0", Min = 0, Max = 86400, SliderMax = 10, Unit = "s", Animatable = false, Description = "When it flashes, in seconds from the start of what it is on.")]
[Param("mode", ParamType.Enum, Default = "over", Choices = "over, add", Animatable = false, Description = "Over turns the picture towards the colour; add adds its light, keeping the picture's detail.")]
public sealed class FlashEffect() : SinglePassEffect("Impact.hlsl", "PsFlash")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => Amount(context, parameters) <= 0;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input) =>
        new() { Values = new Vector4(Amount(context, parameters), 0, 0, 0), FlagX = Choice(parameters, "mode"), Extra = parameters.Color("colour") };

    private static float Amount(EffectContext context, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        return parameters.Float("strength") * (float)ImpactCurves.Flash(
            context.Time.ToSeconds(),
            parameters.Float("trigger"),
            parameters.Float("attack"),
            parameters.Float("hold"),
            parameters.Float("decay"));
    }
}

/// <summary>Red, green and blue pulled apart.</summary>
[VideoEffect("video.chromatic-aberration", Name = "Chromatic aberration", Category = "Impact", Description = "Splits red and blue away from green, outwards from a centre as a cheap lens does or along one direction: the fringe of a hit or a glitch. Keyframe the amount for a burst.")]
[Param("amount", ParamType.Float, Default = "6", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "How far red and blue are pulled apart: at the corners for radial, everywhere for directional.")]
[Param("mode", ParamType.Enum, Default = "radial", Choices = "radial, directional", Animatable = false, Description = "Outwards from the centre, growing towards the edges, or the same everywhere along the angle.")]
[Param("angle", ParamType.Float, Default = "0", Min = -36000, Max = 36000, SliderMax = 360, Unit = "deg", Description = "The direction for directional, clockwise from pointing right.")]
[Param("centre", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The centre for radial, in sequence pixels from the frame centre.")]
public sealed class ChromaticAberrationEffect() : SinglePassEffect("Impact.hlsl", "PsChroma")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => parameters.Float("amount") <= 0;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        float amount = parameters.Float("amount") * context.QualityScale;
        uint mode = Choice(parameters, "mode");
        float radians = parameters.Float("angle") * MathF.PI / 180;
        Vector2 split = mode == 0 ? new Vector2(amount, 0) : new Vector2(MathF.Cos(radians), MathF.Sin(radians)) * amount;
        Vector2 centre = (new Vector2(input.Width, input.Height) / 2) + (parameters.Float2("centre") * context.QualityScale);
        return new() { Values = new Vector4(split, 0, 0), More = new Vector4(centre, 0, 0), FlagX = mode };
    }
}

/// <summary>A few frames of stark, high contrast picture on a hit.</summary>
[VideoEffect("video.impact-frame", Name = "Impact frame", Category = "Impact", Description = "For a few frames from the trigger, turns the picture into stark black and white, its negative, or a few flat tones: the anime impact frame on a hit.")]
[Param("style", ParamType.Enum, Default = "inverted-threshold", Choices = "threshold, inverted-threshold, invert, posterize", Animatable = false, Description = "Black and white by a threshold, the same inverted, the negative, or flat tones.")]
[Param("threshold", ParamType.Float, Default = "0.45", Min = 0, Max = 1, Description = "The brightness between black and white, for the threshold styles.")]
[Param("levels", ParamType.Int, Default = "3", Min = 2, Max = 16, Description = "Tones, for posterize.")]
[Param("colour", ParamType.Color, Default = "#FFFFFF", Description = "The colour of the lights; red for a boss.")]
[Param("trigger", ParamType.Float, Default = "0", Min = 0, Max = 86400, SliderMax = 10, Unit = "s", Animatable = false, Description = "When it starts, in seconds from the start of what it is on.")]
[Param("frames", ParamType.Int, Default = "2", Min = 1, Max = 120, SliderMax = 12, Animatable = false, Description = "How many frames it lasts, at the sequence's rate.")]
public sealed class ImpactFrameEffect() : SinglePassEffect("Impact.hlsl", "PsImpact")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);

        // Counted in whole frames of the sequence, so it lasts exactly that many at any rate. The
        // trigger is a float, so the frame it lands on is found to the nearest half frame.
        double frame = (context.Time.ToSeconds() - parameters.Float("trigger")) * context.FrameRate.ToDouble();
        return frame < -0.5 || frame >= parameters.Int("frames") - 0.5;
    }

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Vector4 colour = parameters.Color("colour");
        Vector4 straight = colour.W > 0 ? colour / colour.W : Vector4.Zero;
        return new()
        {
            Values = new Vector4(parameters.Float("threshold"), parameters.Int("levels"), 0, 0),
            FlagX = Choice(parameters, "style"),
            Extra = new Vector4(LinearToSrgb(straight.X), LinearToSrgb(straight.Y), LinearToSrgb(straight.Z), 1),
        };
    }

    private static float LinearToSrgb(float linear) =>
        linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1 / 2.4f)) - 0.055f;
}
