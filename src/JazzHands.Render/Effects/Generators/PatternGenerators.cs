using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Generators;

/// <summary>A generator that is one pixel shader with no input.</summary>
public abstract class ShaderGenerator : VideoGenerator
{
    private readonly PassDescriptor _pass;

    /// <summary>Creates the generator over one entry point of a shader file.</summary>
    protected ShaderGenerator(string file, string pixel)
    {
        _pass = new PassDescriptor(file, pixel);
        Passes = [_pass];
    }

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; }

    /// <inheritdoc />
    public sealed override void Render(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(output);

        EffectValues values = Values(context, parameters, output);
        context.Draw(_pass, output, in values);
    }

    /// <summary>The constants for this frame.</summary>
    protected abstract EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget output);

    /// <summary>A linear premultiplied colour as straight perceptual RGBA, which the generator shaders blend in.</summary>
    protected static Vector4 Perceptual(Vector4 linear)
    {
        if (linear.W <= 0.0f)
        {
            return Vector4.Zero;
        }

        return new Vector4(
            ParamValues.LinearToSrgb(Math.Clamp(linear.X / linear.W, 0.0f, 1.0f)),
            ParamValues.LinearToSrgb(Math.Clamp(linear.Y / linear.W, 0.0f, 1.0f)),
            ParamValues.LinearToSrgb(Math.Clamp(linear.Z / linear.W, 0.0f, 1.0f)),
            linear.W);
    }

    /// <summary>A point in sequence pixels from the frame centre, in texels of a target.</summary>
    protected static Vector2 Texels(Vector2 fromCentre, EffectContext context, RenderTarget target) =>
        (new Vector2(target.Width, target.Height) / 2.0f) + (fromCentre * context.QualityScale);
}

/// <summary>A linear or radial blend between two colours.</summary>
[Generator("gen.gradient", Name = "Gradient", Category = "Generators", Description = "A linear or radial blend between two colours, for backgrounds, skies and fades.")]
[Param("kind", ParamType.Enum, Default = "linear", Choices = "linear, radial", Animatable = false, Description = "A straight blend, or rings out from the start point.")]
[Param("start-colour", ParamType.Color, Default = "#1B2A6B", Description = "The colour at the start point.")]
[Param("end-colour", ParamType.Color, Default = "#E86F3A", Description = "The colour at the end point.")]
[Param("start", ParamType.Point, Default = "0, -540", Unit = "px", Description = "Where the start colour is, in sequence pixels from the frame centre; the centre of a radial gradient.")]
[Param("end", ParamType.Point, Default = "0, 540", Unit = "px", Description = "Where the end colour is; its distance from the start is a radial gradient's radius.")]
public sealed class GradientGenerator() : ShaderGenerator("Generators.hlsl", "PsGradient")
{
    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        Vector2 start = Texels(parameters.Float2("start"), context, output);
        Vector2 end = Texels(parameters.Float2("end"), context, output);
        return new()
        {
            Values = Perceptual(parameters.Color("start-colour")),
            More = Perceptual(parameters.Color("end-colour")),
            Extra = new Vector4(start, end.X, end.Y),
            FlagX = parameters.Enum("kind") == "radial" ? 1u : 0u,
        };
    }
}

/// <summary>Smooth fractal noise: clouds, smoke, texture.</summary>
[Generator("gen.noise", Name = "Noise", Category = "Generators", Description = "Smooth fractal noise, like clouds or smoke, drifting over time; the same moment always looks the same.")]
[Param("scale", ParamType.Float, Default = "200", Min = 2, Max = 5000, SliderMax = 1000, Unit = "px", Description = "The size of the largest features, in sequence pixels.")]
[Param("detail", ParamType.Int, Default = "4", Min = 1, Max = 8, Description = "Layers of finer detail on top.")]
[Param("contrast", ParamType.Float, Default = "1.5", Min = 0, Max = 10, SliderMax = 4, Description = "How far the brightest and darkest parts are pushed apart.")]
[Param("drift", ParamType.Point, Default = "20, 10", Unit = "px", Description = "How far the pattern moves each second, in sequence pixels.")]
[Param("colour", ParamType.Bool, Default = "false", Animatable = false, Description = "Coloured noise instead of grey.")]
public sealed class NoiseGenerator() : ShaderGenerator("Generators.hlsl", "PsNoise")
{
    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        float seconds = (float)context.Time.ToSeconds();
        Vector2 drift = parameters.Float2("drift") * seconds * context.QualityScale;
        return new()
        {
            Values = new Vector4(parameters.Float("scale") * context.QualityScale, parameters.Float("contrast"), context.Seed & 0x3FF, parameters.Int("detail")),
            More = new Vector4(drift, 0, 0),
            FlagX = parameters.Bool("colour") ? 1u : 0u,
        };
    }
}

/// <summary>A checkerboard of two colours.</summary>
[Generator("gen.checkerboard", Name = "Checkerboard", Category = "Generators", Description = "A checkerboard of two colours, for backgrounds and for checking transparency and scaling.")]
[Param("size", ParamType.Float, Default = "80", Min = 1, Max = 5000, SliderMax = 400, Unit = "px", Description = "The width of a square, in sequence pixels.")]
[Param("first-colour", ParamType.Color, Default = "#DDDDDD", Description = "The squares meeting at the centre.")]
[Param("second-colour", ParamType.Color, Default = "#555555", Description = "The other squares.")]
[Param("offset", ParamType.Point, Default = "0, 0", Unit = "px", Description = "Moves the board, in sequence pixels.")]
public sealed class CheckerboardGenerator() : ShaderGenerator("Generators.hlsl", "PsChecker")
{
    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget output)
    {
        Vector2 offset = parameters.Float2("offset") * context.QualityScale;
        return new()
        {
            Values = Perceptual(parameters.Color("first-colour")),
            More = Perceptual(parameters.Color("second-colour")),
            Extra = new Vector4(parameters.Float("size") * context.QualityScale, offset.X, offset.Y, 0),
        };
    }
}
