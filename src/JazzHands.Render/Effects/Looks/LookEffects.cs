using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;
using JazzHands.Render.Effects.Blur;

namespace JazzHands.Render.Effects.Looks;

/// <summary>What the looks share: a seed that changes a number of times a second.</summary>
internal static class LookSeed
{
    /// <summary>
    /// The seed for a moment: the effect's own (from its id and its seed parameter) mixed with the
    /// step, which counts up <paramref name="rate"/> times a second from the owner's start.
    /// </summary>
    public static uint At(EffectContext context, ParameterSet parameters, double rate)
    {
        long step = rate > 0 ? (long)Math.Floor((context.Time.ToSeconds() * rate) + 1e-9) : 0;
        uint mixed = unchecked((uint)context.Seed + ((uint)parameters.Int("seed") * 7919u));
        return unchecked(mixed ^ ((uint)step * 2654435761u) ^ (uint)(step >> 32));
    }
}

/// <summary>Digital glitch: torn rows, displaced blocks, split colour and jittered lines.</summary>
[VideoEffect("video.glitch", Name = "Glitch", Category = "Looks", Seeded = true, Description = "A broken digital signal: rows torn sideways, blocks of picture moved, red and blue split apart and every line nudged, changing speed times a second. Keyframe the amount for bursts.")]
[Param("amount", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How broken; 0 is clean.")]
[Param("block", ParamType.Float, Default = "32", Min = 2, Max = 1000, SliderMax = 200, Unit = "px", Description = "The size of the torn rows and moved blocks, in sequence pixels.")]
[Param("split", ParamType.Float, Default = "12", Min = 0, Max = 500, SliderMax = 60, Unit = "px", Description = "How far red and blue are pulled apart at full amount.")]
[Param("jitter", ParamType.Float, Default = "3", Min = 0, Max = 200, SliderMax = 20, Unit = "px", Description = "How far each line is nudged at full amount.")]
[Param("speed", ParamType.Float, Default = "12", Min = 0, Max = 120, SliderMax = 30, Unit = "Hz", Description = "How many times a second the damage changes; 0 holds one pattern.")]
[Param("seed", ParamType.Int, Default = "1", Min = 0, Max = 100000, Animatable = false, Description = "Change it for a different pattern with the same settings.")]
public sealed class GlitchEffect() : SinglePassEffect("Looks.hlsl", "PsGlitch")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => parameters.Float("amount") <= 0;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(context);
        float scale = context.QualityScale;
        return new()
        {
            Values = new Vector4(parameters.Float("amount"), parameters.Float("block") * scale, parameters.Float("split") * scale, parameters.Float("jitter") * scale),
            FlagX = LookSeed.At(context, parameters, parameters.Float("speed")),
        };
    }
}

/// <summary>An old television: curved glass, scanlines, a phosphor mask and a glow.</summary>
[VideoEffect("video.crt", Name = "CRT", Category = "Looks", Description = "The picture on an old tube television or arcade monitor: bulging glass, dark scanlines, red, green and blue phosphor columns, a soft glow and darker corners.")]
[Param("curvature", ParamType.Float, Default = "0.35", Min = 0, Max = 2, SliderMax = 1, Description = "How much the glass bulges; the corners go black.")]
[Param("scanlines", ParamType.Float, Default = "0.45", Min = 0, Max = 1, Description = "How dark the gaps between lines are.")]
[Param("lines", ParamType.Float, Default = "240", Min = 16, Max = 2160, SliderMax = 720, Description = "How many lines down the screen; 240 is an arcade monitor, 480 a television.")]
[Param("mask", ParamType.Float, Default = "0.3", Min = 0, Max = 1, Description = "How strongly the phosphor columns show.")]
[Param("mask-size", ParamType.Float, Default = "3", Min = 1, Max = 40, SliderMax = 10, Unit = "px", Description = "The width of one phosphor column, in sequence pixels.")]
[Param("glow", ParamType.Float, Default = "0.4", Min = 0, Max = 2, SliderMax = 1, Description = "How much light bleeds from each point to its neighbours.")]
[Param("vignette", ParamType.Float, Default = "0.4", Min = 0, Max = 1, Description = "How much darker the corners are.")]
public sealed class CrtEffect() : SinglePassEffect("Looks.hlsl", "PsCrt")
{
    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new()
        {
            Values = new Vector4(parameters.Float("curvature"), parameters.Float("scanlines"), parameters.Float("lines"), parameters.Float("mask")),
            More = new Vector4(parameters.Float("glow"), parameters.Float("vignette"), parameters.Float("mask-size") * context.QualityScale, 0),
        };
    }
}

/// <summary>A worn videotape: wobble, smeared colour, grain and a tracking band.</summary>
[VideoEffect("video.vhs", Name = "VHS", Category = "Looks", Seeded = true, Description = "A worn videotape: the picture wobbles, colour smears to the right, grain crawls, and a torn, noisy tracking band rolls down the frame. Add gen.date-stamp for the camcorder date.")]
[Param("wobble", ParamType.Float, Default = "3", Min = 0, Max = 100, SliderMax = 20, Unit = "px", Description = "How far the picture sways side to side.")]
[Param("bleed", ParamType.Float, Default = "10", Min = 0, Max = 200, SliderMax = 40, Unit = "px", Description = "How far colour smears to the right.")]
[Param("noise", ParamType.Float, Default = "0.35", Min = 0, Max = 1, Description = "How much grain.")]
[Param("tracking", ParamType.Float, Default = "0.6", Min = 0, Max = 1, Description = "How torn the tracking band is; 0 for none.")]
[Param("roll", ParamType.Float, Default = "0.15", Min = 0, Max = 10, SliderMax = 2, Unit = "Hz", Description = "How many times a second the tracking band rolls down the frame.")]
[Param("saturation", ParamType.Float, Default = "0.8", Min = 0, Max = 2, Description = "How much colour is left; tape loses some.")]
[Param("seed", ParamType.Int, Default = "1", Min = 0, Max = 100000, Animatable = false, Description = "Change it for different noise with the same settings.")]
public sealed class VhsEffect() : SinglePassEffect("Looks.hlsl", "PsVhs")
{
    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(context);
        float scale = context.QualityScale;
        double seconds = context.Time.ToSeconds();
        double roll = parameters.Float("roll") * seconds;
        return new()
        {
            Values = new Vector4(parameters.Float("wobble") * scale, parameters.Float("bleed") * scale, parameters.Float("noise"), parameters.Float("tracking")),
            More = new Vector4((float)(seconds % 3600), (float)((roll - Math.Floor(roll)) * 1.2) - 0.1f, parameters.Float("saturation"), 0),
            FlagX = LookSeed.At(context, parameters, context.FrameRate.ToDouble()),
        };
    }
}

/// <summary>A shine that sweeps across the picture, only where it is.</summary>
[VideoEffect("video.light-sweep", Name = "Light sweep", Category = "Looks", Description = "A band of light sweeping across a logo or a title, only where it has picture: the shine on a reveal. It crosses once from the trigger over the duration, and again every repeat.")]
[Param("angle", ParamType.Float, Default = "20", Min = -36000, Max = 36000, SliderMax = 360, Unit = "deg", Description = "The direction it travels, clockwise from pointing right.")]
[Param("width", ParamType.Float, Default = "160", Min = 1, Max = 4000, SliderMax = 600, Unit = "px", Description = "How wide the band is, in sequence pixels.")]
[Param("softness", ParamType.Float, Default = "0.8", Min = 0, Max = 1, Description = "How soft its edges are.")]
[Param("intensity", ParamType.Float, Default = "1.5", Min = 0, Max = 20, SliderMax = 5, Description = "How bright the shine is.")]
[Param("colour", ParamType.Color, Default = "#FFFFFF", Description = "The shine's colour.")]
[Param("trigger", ParamType.Float, Default = "0", Min = 0, Max = 86400, SliderMax = 10, Unit = "s", Animatable = false, Description = "When the first sweep starts, in seconds from the start of what it is on.")]
[Param("duration", ParamType.Float, Default = "0.8", Min = 0.01, Max = 60, SliderMax = 5, Unit = "s", Description = "How long one sweep takes to cross.")]
[Param("repeat", ParamType.Float, Default = "0", Min = 0, Max = 600, SliderMax = 10, Unit = "s", Animatable = false, Description = "Seconds from one sweep's start to the next; 0 sweeps once.")]
public sealed class LightSweepEffect() : SinglePassEffect("Looks.hlsl", "PsLightSweep")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => Progress(context, parameters) is null;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        float radians = parameters.Float("angle") * MathF.PI / 180;
        var direction = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        float width = parameters.Float("width") * context.QualityScale;

        // From just before the frame's first corner along the direction to just past its last.
        float[] corners = [0, Vector2.Dot(new Vector2(input.Width, 0), direction), Vector2.Dot(new Vector2(0, input.Height), direction), Vector2.Dot(new Vector2(input.Width, input.Height), direction)];
        float from = corners.Min() - width;
        float to = corners.Max() + width;
        float middle = from + ((to - from) * (float)Progress(context, parameters)!.Value);

        Vector4 colour = parameters.Color("colour");
        return new()
        {
            Values = new Vector4(middle, width, parameters.Float("intensity"), parameters.Float("softness")),
            More = new Vector4(direction, 0, 0),
            Extra = colour.W > 0 ? colour / colour.W : Vector4.Zero,
        };
    }

    /// <summary>How far across the current sweep is, 0 to 1, or null between sweeps.</summary>
    internal static double? Progress(EffectContext context, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        double since = context.Time.ToSeconds() - parameters.Float("trigger");
        double repeat = parameters.Float("repeat");
        if (since < 0)
        {
            return null;
        }

        if (repeat > 0)
        {
            since %= repeat;
        }

        double progress = since / parameters.Float("duration");
        return progress <= 1.0 ? progress : null;
    }
}

/// <summary>Cinema bars, to any aspect, sliding in as the amount rises.</summary>
[VideoEffect("video.letterbox", Name = "Letterbox", Category = "Looks", Description = "Bars that crop the frame to a wider (or narrower) aspect, as a film does. Keyframe the amount from 0 to 1 to slide them in for a cinematic moment.")]
[Param("aspect", ParamType.Float, Default = "2.39", Min = 0.1, Max = 10, SliderMax = 4, Description = "The shape left between the bars, width over height: 2.39 scope, 1.85 flat, 1 square.")]
[Param("amount", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "How far in the bars are; 0 is none.")]
[Param("colour", ParamType.Color, Default = "#000000", Description = "The bars' colour.")]
public sealed class LetterboxEffect() : SinglePassEffect("Looks.hlsl", "PsLetterbox")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => parameters.Float("amount") <= 0;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        (float top, float side) = Bars(input.Width, input.Height, parameters.Float("aspect"), parameters.Float("amount"));
        return new() { Values = new Vector4(top, side, 0, 0), Extra = parameters.Color("colour") };
    }

    /// <summary>The bars' height top and bottom and width left and right, in pixels of a frame.</summary>
    public static (float Top, float Side) Bars(float width, float height, float aspect, float amount)
    {
        float frame = width / height;
        return aspect >= frame
            ? ((height - (width / aspect)) / 2 * amount, 0)
            : (0, (width - (height * aspect)) / 2 * amount);
    }
}

/// <summary>Light spilling from the brightest parts at several sizes at once.</summary>
[VideoEffect("video.bloom", Name = "Bloom", Category = "Looks", Description = "Light spilling from the brightest parts of the picture, a tight core and a wide haze together, as a lens and an eye see it: explosions, muzzle flashes, neon, the sun. Only what is over the threshold blooms.")]
[Param("threshold", ParamType.Float, Default = "0.8", Min = 0, Max = 8, SliderMax = 2, Description = "How bright, in linear light, a part has to be to bloom; 1 is white.")]
[Param("knee", ParamType.Float, Default = "0.3", Min = 0, Max = 4, SliderMax = 1, Description = "How gradually it starts over the threshold.")]
[Param("radius", ParamType.Float, Default = "60", Min = 1, Max = 1000, SliderMax = 300, Unit = "px", Description = "How far the widest haze reaches, in sequence pixels.")]
[Param("intensity", ParamType.Float, Default = "1", Min = 0, Max = 20, SliderMax = 4, Description = "How strong it is.")]
[Param("tint", ParamType.Color, Default = "#FFFFFF", Description = "Its colour; white keeps the picture's own.")]
public sealed class BloomEffect : VideoEffect
{
    private static readonly PassDescriptor Bright = new("BlurFamily.hlsl", "PsBright");
    private static readonly PassDescriptor Combine = new("Looks.hlsl", "PsBloom");

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; } = [Bright, .. GaussianBlurEffect.BlurPasses, Combine];

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        float intensity = parameters.Float("intensity");
        if (intensity <= 0.0f)
        {
            context.Copy(input, output);
            return;
        }

        RenderTarget bright = context.Rent(input.Width, input.Height);
        RenderTarget small = context.Rent(input.Width, input.Height);
        RenderTarget middle = context.Rent(input.Width, input.Height);
        RenderTarget wide = context.Rent(input.Width, input.Height);
        var brightness = new EffectValues { Values = new Vector4(parameters.Float("threshold"), parameters.Float("knee"), 0, 0) };
        context.Draw(Bright, bright, in brightness, input);

        // Three sizes, a sixteenth, a quarter and the whole radius: a core and a haze.
        float sigma = parameters.Float("radius") * context.QualityScale / 3.0f;
        GaussianBlurEffect.Blur(context, bright, small, sigma / 16, repeat: false);
        GaussianBlurEffect.Blur(context, bright, middle, sigma / 4, repeat: false);
        GaussianBlurEffect.Blur(context, bright, wide, sigma, repeat: false);

        Vector4 tint = parameters.Color("tint");
        var combine = new EffectValues { Values = new Vector4(intensity, 0, 0, 0), More = tint.W > 0 ? tint / tint.W : Vector4.Zero };
        context.Draw(Combine, output, in combine, input, small, middle, wide);

        context.Return(wide);
        context.Return(middle);
        context.Return(small);
        context.Return(bright);
    }
}

/// <summary>Smooths the steps compression leaves in gradients, and dithers so they stay smooth.</summary>
[VideoEffect("video.deband", Name = "Deband", Category = "Looks", Description = "Smooths the visible steps that compression cuts into smooth gradients (8-bit game skies, fog, dark scenes), leaving detail alone, and adds a fine dither so the export does not band them again.")]
[Param("threshold", ParamType.Float, Default = "0.02", Min = 0, Max = 0.2, SliderMax = 0.08, Description = "How different neighbours may be and still count as one smooth area; higher smooths more and risks detail.")]
[Param("radius", ParamType.Float, Default = "16", Min = 1, Max = 200, SliderMax = 64, Unit = "px", Description = "How far it looks for neighbours; wider bands need more.")]
[Param("dither", ParamType.Float, Default = "1", Min = 0, Max = 8, SliderMax = 4, Description = "How strong the dither is, in 8-bit steps.")]
[Param("seed", ParamType.Int, Default = "1", Min = 0, Max = 100000, Animatable = false, Description = "Change it for a different dither pattern.")]
public sealed class DebandEffect() : SinglePassEffect("Looks.hlsl", "PsDeband")
{
    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new()
        {
            Values = new Vector4(parameters.Float("threshold"), parameters.Float("radius") * context.QualityScale, parameters.Float("dither"), 0),
            FlagX = LookSeed.At(context, parameters, context.FrameRate.ToDouble()),
        };
    }
}
