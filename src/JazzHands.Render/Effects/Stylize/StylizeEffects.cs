using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Stylize;

/// <summary>Turns light dark and dark light, in full or partly, by colour or by brightness alone.</summary>
[VideoEffect("video.invert", Name = "Invert", Category = "Stylize", Description = "Inverts the picture's colours, or only its brightness so the hues stay, by any amount.")]
[Param("amount", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "How far towards the inverse, 0 none to 1 fully.")]
[Param("mode", ParamType.Enum, Default = "colour", Choices = "colour, brightness", Animatable = false, Description = "Invert every channel, or only the brightness, keeping hue.")]
public sealed class InvertEffect() : SinglePassEffect("Stylize.hlsl", "PsInvert")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => parameters.Float("amount") <= 0.0f;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input) =>
        new() { Values = new Vector4(parameters.Float("amount"), 0, 0, 0), FlagX = Choice(parameters, "mode") };
}

/// <summary>Cuts each channel to a few levels, for a poster or screen print look.</summary>
[VideoEffect("video.posterize", Name = "Posterize", Category = "Stylize", Description = "Reduces each colour channel to a few levels, for a flat poster or print look.")]
[Param("levels", ParamType.Int, Default = "6", Min = 2, Max = 64, SliderMax = 16, Description = "Levels per channel; fewer is flatter.")]
public sealed class PosterizeEffect() : SinglePassEffect("Stylize.hlsl", "PsPosterize")
{
    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input) =>
        new() { Values = new Vector4(parameters.Int("levels"), 0, 0, 0) };
}

/// <summary>Big square pixels, laid out from the frame centre.</summary>
[VideoEffect("video.pixelate", Name = "Pixelate", Category = "Stylize", Description = "Breaks the picture into large square cells, for censoring or a retro look.")]
[Param("size", ParamType.Float, Default = "16", Min = 1, Max = 1000, SliderMax = 200, Unit = "px", Description = "The width of a cell, in sequence pixels.")]
public sealed class PixelateEffect() : SinglePassEffect("Stylize.hlsl", "PsPixelate")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => parameters.Float("size") * context.QualityScale <= 1.0f;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input) =>
        new() { Values = new Vector4(parameters.Float("size") * context.QualityScale, 0, 0, 0) };
}

/// <summary>Darkens, or lightens, the edges of the frame.</summary>
[VideoEffect("video.vignette", Name = "Vignette", Category = "Stylize", Description = "Darkens the edges of the frame to draw the eye in, or lightens them with a negative amount.")]
[Param("amount", ParamType.Float, Default = "0.5", Min = -1, Max = 1, Description = "How dark the edges go; negative lightens them.")]
[Param("size", ParamType.Float, Default = "0.6", Min = 0, Max = 1.5, Description = "Where the darkening starts, from the centre (0) to the corners (1).")]
[Param("softness", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How gradually it darkens.")]
[Param("roundness", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "1 is a circle; 0 follows the frame's shape.")]
public sealed class VignetteEffect() : SinglePassEffect("Stylize.hlsl", "PsVignette")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => parameters.Float("amount") == 0.0f;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input) =>
        new() { Values = new Vector4(parameters.Float("amount"), parameters.Float("size"), parameters.Float("softness"), parameters.Float("roundness")) };
}

/// <summary>Film grain: seeded, so the same frame always has the same grain.</summary>
[VideoEffect("video.noise", Name = "Noise", Category = "Stylize", Description = "Adds film grain or digital noise; the same frame always gets the same grain.", Seeded = true)]
[Param("amount", ParamType.Float, Default = "0.08", Min = 0, Max = 1, SliderMax = 0.5, Description = "How strong the grain is.")]
[Param("size", ParamType.Float, Default = "1", Min = 1, Max = 50, SliderMax = 10, Unit = "px", Description = "The size of a grain, in sequence pixels.")]
[Param("mono", ParamType.Bool, Default = "true", Animatable = false, Description = "Grey grain, like film; off for coloured noise, like a sensor.")]
[Param("animated", ParamType.Bool, Default = "true", Animatable = false, Description = "A new pattern every frame; off for a still one.")]
public sealed class NoiseEffect() : SinglePassEffect("Stylize.hlsl", "PsNoise")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => parameters.Float("amount") <= 0.0f;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        // The pattern changes sixty times a second of clip time when animated, whatever the frame
        // rate, and never between two renders of the same moment. The seed is a whole number under
        // 2^24, which a float carries exactly to the shader's integer hash.
        uint step = parameters.Bool("animated") ? (uint)Math.Max(0.0, Math.Floor(context.Time.ToSeconds() * 60.0)) : 0u;
        float seed = ((uint)context.Seed + (step * 7919u)) & 0xFFFFFF;
        return new EffectValues
        {
            Values = new Vector4(parameters.Float("amount"), MathF.Max(1.0f, parameters.Float("size") * context.QualityScale), seed, 0),
            FlagX = Flag(parameters.Bool("mono")),
        };
    }
}

/// <summary>Brightness that wavers over time, seeded and smooth.</summary>
[VideoEffect("video.flicker", Name = "Flicker", Category = "Stylize", Description = "Makes the brightness waver like an old projector or a failing light; the same moment always flickers the same way.", Seeded = true)]
[Param("amount", ParamType.Float, Default = "0.2", Min = 0, Max = 1, Description = "How far the brightness swings either way.")]
[Param("speed", ParamType.Float, Default = "12", Min = 0.1, Max = 60, SliderMax = 30, Unit = "Hz", Description = "How many changes a second.")]
public sealed class FlickerEffect() : SinglePassEffect("Stylize.hlsl", "PsFlicker")
{
    /// <summary>The brightness multiplier at a time: value noise, smoothed between random steps.</summary>
    public static float Gain(double seconds, float amount, float speed, int seed)
    {
        double t = seconds * speed;
        long step = (long)Math.Floor(t);
        float f = (float)(t - step);
        f = f * f * (3.0f - (2.0f * f));
        float noise = Lerp(Hash(step, seed), Hash(step + 1, seed), f);
        return 1.0f + (amount * ((2.0f * noise) - 1.0f));
    }

    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => parameters.Float("amount") <= 0.0f;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input) =>
        new() { Values = new Vector4(Gain(context.Time.ToSeconds(), parameters.Float("amount"), parameters.Float("speed"), context.Seed), 0, 0, 0) };

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    /// <summary>A repeatable number from 0 to 1 for a step and a seed.</summary>
    private static float Hash(long step, int seed)
    {
        ulong x = (ulong)step * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)seed * 0xBF58476D1CE4E5B9UL;
        x ^= x >> 31;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 29;
        return (x & 0xFFFFFF) / (float)0xFFFFFF;
    }
}

/// <summary>Outlines: a Sobel filter on brightness.</summary>
[VideoEffect("video.find-edges", Name = "Find Edges", Category = "Stylize", Description = "Finds the outlines in the picture, as light lines on black, coloured lines, or dark lines on white like a drawing.")]
[Param("strength", ParamType.Float, Default = "2", Min = 0, Max = 20, SliderMax = 8, Description = "How bright the lines are.")]
[Param("mix", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "1 shows only the lines, 0 the picture as it was.")]
[Param("coloured", ParamType.Bool, Default = "false", Animatable = false, Description = "Lines in the picture's own colours.")]
[Param("invert", ParamType.Bool, Default = "false", Animatable = false, Description = "Dark lines on white, like a pencil drawing.")]
public sealed class FindEdgesEffect() : SinglePassEffect("Stylize.hlsl", "PsEdges")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => parameters.Float("mix") <= 0.0f;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input) =>
        new()
        {
            Values = new Vector4(parameters.Float("strength"), parameters.Float("mix"), 0, 0),
            FlagX = Flag(parameters.Bool("coloured")),
            FlagY = Flag(parameters.Bool("invert")),
        };
}
