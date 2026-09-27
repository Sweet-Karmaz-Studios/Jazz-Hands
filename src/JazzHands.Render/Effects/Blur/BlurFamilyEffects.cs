using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;
using JazzHands.Render.Effects.Distort;

namespace JazzHands.Render.Effects.Blur;

/// <summary>A smear along one direction, like fast motion.</summary>
[VideoEffect("video.blur.directional", Name = "Directional blur", Category = "Blur", Description = "Smears the picture along one direction, like fast motion past the camera.")]
[Param("angle", ParamType.Float, Default = "0", Min = -36000, Max = 36000, SliderMax = 180, Unit = "deg", Description = "The direction of the smear, 0 across, 90 down.")]
[Param("length", ParamType.Float, Default = "30", Min = 0, Max = 2000, SliderMax = 300, Unit = "px", Description = "How long the smear is, in sequence pixels.")]
public sealed class DirectionalBlurEffect() : SinglePassEffect("BlurFamily.hlsl", "PsDirectional")
{
    /// <summary>At most this many taps each side; longer smears space them out.</summary>
    internal const int MaxTaps = 64;

    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) =>
        parameters.Float("length") * context.QualityScale < 1.0f;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        float half = parameters.Float("length") * context.QualityScale / 2.0f;
        int taps = Math.Clamp((int)MathF.Ceiling(half), 1, MaxTaps);
        float radians = parameters.Float("angle") * MathF.PI / 180.0f;
        var step = new Vector2(MathF.Cos(radians) / input.Width, MathF.Sin(radians) / input.Height) * (half / taps);
        return new() { Values = new Vector4(step, 0, 0), FlagX = (uint)taps };
    }
}

/// <summary>A blur out from a point (zoom) or round it (spin).</summary>
[VideoEffect("video.blur.radial", Name = "Radial blur", Category = "Blur", Description = "Blurs outwards from a point, like a fast zoom, or round it, like a spin.")]
[Param("mode", ParamType.Enum, Default = "zoom", Choices = "zoom, spin", Animatable = false, Description = "Zoom streaks out from the centre; spin turns round it.")]
[Param("amount", ParamType.Float, Default = "10", Min = 0, Max = 100, Unit = "%", Description = "How strong: for zoom, how far the streaks reach; for spin, up to 90 degrees each way.")]
[Param("centre", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The point it blurs from or round, in sequence pixels from the frame centre.")]
public sealed class RadialBlurEffect() : SinglePassEffect("BlurFamily.hlsl", "PsRadial")
{
    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) => parameters.Float("amount") <= 0.0f;

    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        Vector2 centre = LensDistortionEffect.Uv(parameters.Float2("centre"), context, input);
        bool spin = parameters.Enum("mode") == "spin";
        float amount = parameters.Float("amount") / 100.0f;
        float strength = spin ? amount * MathF.PI / 4.0f : amount * 0.5f;
        return new() { Values = new Vector4(centre, strength, 0), FlagX = Flag(spin), FlagY = 48 };
    }
}

/// <summary>Unsharp masking: the picture pushed away from a blurred copy of itself.</summary>
[VideoEffect("video.sharpen", Name = "Sharpen", Category = "Blur", Description = "Crisps up detail by pushing the picture away from a blurred copy of itself (unsharp mask).")]
[Param("amount", ParamType.Float, Default = "100", Min = 0, Max = 500, SliderMax = 300, Unit = "%", Description = "How much detail is added back.")]
[Param("radius", ParamType.Float, Default = "2", Min = 0.1, Max = 100, SliderMax = 20, Unit = "px", Description = "The size of the detail sharpened, in sequence pixels.")]
[Param("threshold", ParamType.Float, Default = "0", Min = 0, Max = 1, SliderMax = 0.2, Description = "Leave alone differences smaller than this, so noise and flat areas stay smooth.")]
public sealed class SharpenEffect : VideoEffect
{
    private static readonly PassDescriptor Combine = new("BlurFamily.hlsl", "PsSharpen");

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; } = [.. GaussianBlurEffect.BlurPasses, Combine];

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        float amount = parameters.Float("amount") / 100.0f;
        float sigma = parameters.Float("radius") * context.QualityScale / 3.0f * 2.0f;
        if (amount <= 0.0f || sigma < 0.1f)
        {
            context.Copy(input, output);
            return;
        }

        RenderTarget soft = context.Rent(input.Width, input.Height);
        GaussianBlurEffect.Blur(context, input, soft, sigma);
        var values = new EffectValues { Values = new Vector4(amount, parameters.Float("threshold"), 0, 0) };
        context.Draw(Combine, output, in values, input, soft);
        context.Return(soft);
    }
}

/// <summary>Bright parts of the picture bloom into a soft glow.</summary>
[VideoEffect("video.glow", Name = "Glow", Category = "Stylize", Description = "Makes the bright parts of the picture bloom into a soft, optionally tinted glow.")]
[Param("threshold", ParamType.Float, Default = "0.6", Min = 0, Max = 4, SliderMax = 1, Description = "How bright, in linear light, a part has to be to glow.")]
[Param("radius", ParamType.Float, Default = "30", Min = 0, Max = 1000, SliderMax = 200, Unit = "px", Description = "How far the glow spreads, in sequence pixels.")]
[Param("intensity", ParamType.Float, Default = "1", Min = 0, Max = 10, SliderMax = 4, Description = "How strong the glow is.")]
[Param("tint", ParamType.Color, Default = "#FFFFFF", Description = "The glow's colour; white keeps the picture's own.")]
public sealed class GlowEffect : VideoEffect
{
    private static readonly PassDescriptor Bright = new("BlurFamily.hlsl", "PsBright");
    private static readonly PassDescriptor Add = new("BlurFamily.hlsl", "PsAddGlow");

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; } = [Bright, .. GaussianBlurEffect.BlurPasses, Add];

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
        RenderTarget glow = context.Rent(input.Width, input.Height);
        var brightness = new EffectValues { Values = new Vector4(parameters.Float("threshold"), 0.1f, 0, 0) };
        context.Draw(Bright, bright, in brightness, input);
        GaussianBlurEffect.Blur(context, bright, glow, parameters.Float("radius") * context.QualityScale / 3.0f, repeat: false);

        Vector4 tint = parameters.Color("tint");
        var add = new EffectValues { Values = new Vector4(intensity, 0, 0, 0), More = tint.W > 0 ? tint / tint.W : Vector4.Zero };
        context.Draw(Add, output, in add, input, glow);

        context.Return(glow);
        context.Return(bright);
    }
}

/// <summary>A soft shadow of the picture's shape, offset behind it.</summary>
[VideoEffect("video.drop-shadow", Name = "Drop shadow", Category = "Stylize", Description = "Casts a soft shadow of the picture's shape behind it, for a picture-in-picture, a logo or a title.")]
[Param("colour", ParamType.Color, Default = "#000000", Description = "The shadow's colour.")]
[Param("opacity", ParamType.Float, Default = "0.6", Min = 0, Max = 1, Description = "How dark the shadow is.")]
[Param("angle", ParamType.Float, Default = "135", Min = -36000, Max = 36000, SliderMax = 360, Unit = "deg", Description = "Where the shadow falls, clockwise from pointing right; 135 is down and left of a light at the top right.")]
[Param("distance", ParamType.Float, Default = "12", Min = 0, Max = 2000, SliderMax = 200, Unit = "px", Description = "How far the shadow falls, in sequence pixels.")]
[Param("softness", ParamType.Float, Default = "16", Min = 0, Max = 1000, SliderMax = 200, Unit = "px", Description = "How soft its edge is, in sequence pixels.")]
public sealed class DropShadowEffect : VideoEffect
{
    private static readonly PassDescriptor Shape = new("BlurFamily.hlsl", "PsShadow");
    private static readonly PassDescriptor Over = new("BlurFamily.hlsl", "PsOver");

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; } = [Shape, .. GaussianBlurEffect.BlurPasses, Over];

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        float opacity = parameters.Float("opacity");
        if (opacity <= 0.0f)
        {
            context.Copy(input, output);
            return;
        }

        // The angle is the direction the shadow falls, clockwise from right with y down, so 135
        // moves it left and down; the shadow at a pixel is the picture's alpha that far back.
        float radians = parameters.Float("angle") * MathF.PI / 180.0f;
        float distance = parameters.Float("distance") * context.QualityScale;
        var offset = new Vector2(MathF.Cos(radians) * distance / input.Width, MathF.Sin(radians) * distance / input.Height);

        Vector4 colour = parameters.Color("colour");
        Vector4 straight = colour.W > 0 ? colour / colour.W : Vector4.Zero;
        var shape = new EffectValues { Values = new Vector4(offset, 0, 0), More = new Vector4(straight.X, straight.Y, straight.Z, 1.0f) * opacity };

        RenderTarget shadow = context.Rent(input.Width, input.Height);
        RenderTarget soft = context.Rent(input.Width, input.Height);
        context.Draw(Shape, shadow, in shape, input);
        GaussianBlurEffect.Blur(context, shadow, soft, parameters.Float("softness") * context.QualityScale / 3.0f, repeat: false);

        var over = default(EffectValues);
        context.Draw(Over, output, in over, input, soft);

        context.Return(soft);
        context.Return(shadow);
    }
}
