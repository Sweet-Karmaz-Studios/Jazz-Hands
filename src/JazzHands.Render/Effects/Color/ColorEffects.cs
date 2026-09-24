using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using JazzHands.Core.Effects;
using JazzHands.Render.Color;
using JazzHands.Render.Compositing;
using Vortice.Direct3D11;

namespace JazzHands.Render.Effects.Color;

/// <summary>The constant buffer Grading.hlsl shares: five float4s and four flags.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GradingValues
{
    /// <summary>The first vector.</summary>
    public Vector4 A;

    /// <summary>The second.</summary>
    public Vector4 B;

    /// <summary>The third.</summary>
    public Vector4 C;

    /// <summary>The fourth.</summary>
    public Vector4 D;

    /// <summary>The fifth.</summary>
    public Vector4 E;

    /// <summary>The first flag.</summary>
    public uint FlagX;

    /// <summary>The second flag.</summary>
    public uint FlagY;

    /// <summary>The third flag.</summary>
    public uint FlagZ;

    /// <summary>The fourth flag.</summary>
    public uint FlagW;

    /// <summary>A 3x3 matrix into A, B and C, as the shader's Transform reads it.</summary>
    public void SetMatrix(Matrix4x4 matrix) => (A, B, C) = ColorMath.Rows(matrix);
}

/// <summary>
/// A colour effect that is one pass of Grading.hlsl, optionally with a texture of its own (a baked
/// curve, a LUT) at t1. When <see cref="PassesThrough"/> says the settings change nothing, the
/// input is copied.
/// </summary>
public abstract class GradingEffect : VideoEffect
{
    private readonly PassDescriptor _pass;

    /// <summary>Creates the effect over one entry point of Grading.hlsl.</summary>
    protected GradingEffect(string pixel)
    {
        _pass = new PassDescriptor("Grading.hlsl", pixel);
        Passes = [_pass];
    }

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; }

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (PassesThrough(context, parameters))
        {
            context.Copy(input, output);
            return;
        }

        GradingValues values = Values(context, parameters);
        ID3D11ShaderResourceView? resource = Resource(context, parameters);
        if (resource is null)
        {
            context.Draw(_pass, output, in values, input);
        }
        else
        {
            context.Draw(_pass, output, in values, [input], [resource]);
        }
    }

    /// <summary>The constants for this frame.</summary>
    protected abstract GradingValues Values(EffectContext context, ParameterSet parameters);

    /// <summary>A texture of the effect's own for t1, or null for none.</summary>
    protected virtual ID3D11ShaderResourceView? Resource(EffectContext context, ParameterSet parameters) => null;

    /// <summary>True when these settings leave the picture exactly as it is.</summary>
    protected virtual bool PassesThrough(EffectContext context, ParameterSet parameters) => false;

    /// <summary>1 for true, 0 for false.</summary>
    protected static uint Flag(bool value) => value ? 1u : 0u;
}

/// <summary>Exposure, contrast, white balance, saturation and vibrance: the first stop of a grade.</summary>
[VideoEffect("color.basic", Name = "Basic Correction", Category = "Color", Description = "Exposure, contrast about mid grey, temperature and tint, saturation and vibrance: the first corrections of a grade, in linear light.")]
[Param("exposure", ParamType.Float, Default = "0", Min = -8, Max = 8, SliderMax = 4, Unit = "stops", Description = "Brighter or darker in stops, as a camera's exposure: each stop doubles or halves the light.")]
[Param("contrast", ParamType.Float, Default = "1", Min = 0, Max = 4, SliderMax = 2, Description = "Spreads or squeezes the tones about 18% grey; 1 leaves them, above 1 is punchier.")]
[Param("temperature", ParamType.Float, Default = "0", Min = -100, Max = 100, Description = "Warmer (positive) or cooler (negative), as a white balance would.")]
[Param("tint", ParamType.Float, Default = "0", Min = -100, Max = 100, Description = "Towards magenta (positive) or green (negative), for lights that are off the daylight line.")]
[Param("saturation", ParamType.Float, Default = "1", Min = 0, Max = 4, SliderMax = 2, Description = "How colourful: 0 is black and white, 1 as shot.")]
[Param("vibrance", ParamType.Float, Default = "0", Min = -1, Max = 1, Description = "Saturation that goes mostly to muted colours, sparing skin and what is already vivid.")]
public sealed class BasicCorrectionEffect : GradingEffect
{
    /// <summary>Creates the effect.</summary>
    public BasicCorrectionEffect()
        : base("PsBasic")
    {
    }

    /// <inheritdoc />
    protected override GradingValues Values(EffectContext context, ParameterSet parameters)
    {
        var values = new GradingValues
        {
            D = new Vector4(
                MathF.Pow(2.0f, parameters.Float("exposure")),
                parameters.Float("contrast"),
                parameters.Float("saturation"),
                parameters.Float("vibrance")),
        };
        values.SetMatrix(ColorMath.TemperatureTint(parameters.Float("temperature"), parameters.Float("tint")));
        return values;
    }

    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) =>
        parameters.Float("exposure") == 0 && parameters.Float("contrast") == 1
        && parameters.Float("temperature") == 0 && parameters.Float("tint") == 0
        && parameters.Float("saturation") == 1 && parameters.Float("vibrance") == 0;
}

/// <summary>Lift, gamma, gain and offset wheels, with saturation, contrast and pivot.</summary>
[VideoEffect("color.wheels", Name = "Color Wheels", Category = "Color", Description = "Lift, gamma and gain move the shadows, mid tones and highlights, each by colour and by brightness; offset moves everything. The colourist's primary grade.")]
[Param("lift", ParamType.Float4, Default = "0, 0, 0, 0", Min = -1, Max = 1, Description = "Shadows: red, green, blue and master, raising or lowering the blacks with white held.")]
[Param("gamma", ParamType.Float4, Default = "0, 0, 0, 0", Min = -0.99, Max = 4, SliderMax = 1, Description = "Mid tones: red, green, blue and master, brighter when positive, with black and white held.")]
[Param("gain", ParamType.Float4, Default = "0, 0, 0, 0", Min = -1, Max = 4, SliderMax = 1, Description = "Highlights: red, green, blue and master, scaling everything from black.")]
[Param("offset", ParamType.Float4, Default = "0, 0, 0, 0", Min = -1, Max = 1, Description = "Everything: red, green, blue and master, added evenly to every tone.")]
[Param("saturation", ParamType.Float, Default = "1", Min = 0, Max = 4, SliderMax = 2, Description = "How colourful after the wheels: 0 is black and white.")]
[Param("contrast", ParamType.Float, Default = "1", Min = 0, Max = 4, SliderMax = 2, Description = "Spreads or squeezes the tones about the pivot.")]
[Param("pivot", ParamType.Float, Default = "0.435", Min = 0, Max = 1, Description = "The tone contrast turns about; 0.435 is 18% grey.")]
public sealed class ColorWheelsEffect : GradingEffect
{
    /// <summary>Creates the effect.</summary>
    public ColorWheelsEffect()
        : base("PsWheels")
    {
    }

    /// <inheritdoc />
    protected override GradingValues Values(EffectContext context, ParameterSet parameters) => new()
    {
        A = parameters.Float4("lift"),
        B = parameters.Float4("gamma"),
        C = parameters.Float4("gain"),
        D = parameters.Float4("offset"),
        E = new Vector4(parameters.Float("saturation"), parameters.Float("contrast"), parameters.Float("pivot"), 0),
    };

    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) =>
        parameters.Float4("lift") == Vector4.Zero && parameters.Float4("gamma") == Vector4.Zero
        && parameters.Float4("gain") == Vector4.Zero && parameters.Float4("offset") == Vector4.Zero
        && parameters.Float("saturation") == 1 && parameters.Float("contrast") == 1;
}

/// <summary>A secondary: a range of hue, saturation and brightness, changed on its own.</summary>
[VideoEffect("color.hsl", Name = "HSL Qualifier", Category = "Color", Description = "Picks out a range of colours by hue, saturation and brightness and changes only those: a greener grass, a bluer sky, a skin tone; view the matte to see what is picked.")]
[Param("hue", ParamType.Float, Default = "120", Min = 0, Max = 360, Unit = "deg", Description = "The centre of the hues picked: 0 red, 60 yellow, 120 green, 180 cyan, 240 blue, 300 magenta.")]
[Param("hue-width", ParamType.Float, Default = "30", Min = 0, Max = 180, Unit = "deg", Description = "How far either side of the centre is fully picked.")]
[Param("hue-softness", ParamType.Float, Default = "20", Min = 0, Max = 180, Unit = "deg", Description = "How gradually it fades out past the width.")]
[Param("sat-low", ParamType.Float, Default = "0.15", Min = 0, Max = 1, Description = "The least saturated colour picked; raise it to leave greys alone.")]
[Param("sat-high", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "The most saturated colour picked.")]
[Param("sat-softness", ParamType.Float, Default = "0.1", Min = 0, Max = 1, Description = "How gradually saturation fades in and out of the pick.")]
[Param("luma-low", ParamType.Float, Default = "0", Min = 0, Max = 1, Description = "The darkest tone picked.")]
[Param("luma-high", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "The brightest tone picked.")]
[Param("luma-softness", ParamType.Float, Default = "0.1", Min = 0, Max = 1, Description = "How gradually brightness fades in and out of the pick.")]
[Param("invert", ParamType.Bool, Default = "false", Animatable = false, Description = "Change everything except the pick.")]
[Param("hue-shift", ParamType.Float, Default = "0", Min = -180, Max = 180, Unit = "deg", Description = "Turns the picked colours round the hue circle.")]
[Param("saturation", ParamType.Float, Default = "1", Min = 0, Max = 4, SliderMax = 2, Description = "How colourful the picked colours are.")]
[Param("lightness", ParamType.Float, Default = "0", Min = -1, Max = 1, Description = "Brighter or darker, the picked colours only.")]
[Param("view", ParamType.Enum, Default = "result", Choices = "result, matte", Animatable = false, Description = "The corrected picture, or the pick as white on black.")]
public sealed class HslQualifierEffect : GradingEffect
{
    /// <summary>Creates the effect.</summary>
    public HslQualifierEffect()
        : base("PsHsl")
    {
    }

    /// <inheritdoc />
    protected override GradingValues Values(EffectContext context, ParameterSet parameters) => new()
    {
        A = new Vector4(parameters.Float("hue") / 360.0f, parameters.Float("hue-width") / 360.0f, parameters.Float("hue-softness") / 360.0f, 0),
        B = new Vector4(parameters.Float("sat-low"), parameters.Float("sat-high"), parameters.Float("sat-softness"), 0),
        C = new Vector4(parameters.Float("luma-low"), parameters.Float("luma-high"), parameters.Float("luma-softness"), 0),
        D = new Vector4(parameters.Float("hue-shift") / 360.0f, parameters.Float("saturation"), parameters.Float("lightness"), 0),
        FlagX = Flag(parameters.Bool("invert")),
        FlagY = Flag(parameters.Enum("view") == "matte"),
    };

    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters) =>
        parameters.Enum("view") != "matte"
        && parameters.Float("hue-shift") == 0 && parameters.Float("saturation") == 1 && parameters.Float("lightness") == 0;
}

/// <summary>Makes a picked colour neutral: white balance from something that should be white or grey.</summary>
[VideoEffect("color.white-balance", Name = "White Balance", Category = "Color", Description = "Makes a colour that should be neutral (a white wall, a grey card) neutral, and the rest of the picture with it; pick it from the preview.")]
[Param("neutral", ParamType.Color, Default = "#FFFFFF", Description = "The colour that should be white or grey, picked from the picture; white changes nothing.")]
[Param("amount", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "How much of the correction to apply.")]
public sealed class WhiteBalanceEffect : GradingEffect
{
    /// <summary>Creates the effect.</summary>
    public WhiteBalanceEffect()
        : base("PsWhiteBalance")
    {
    }

    /// <inheritdoc />
    protected override GradingValues Values(EffectContext context, ParameterSet parameters)
    {
        Vector4 neutral = parameters.Color("neutral");
        var values = new GradingValues { D = new Vector4(parameters.Float("amount"), 0, 0, 0) };
        values.SetMatrix(ColorMath.Neutralise(new Vector3(neutral.X, neutral.Y, neutral.Z)));
        return values;
    }

    /// <inheritdoc />
    protected override bool PassesThrough(EffectContext context, ParameterSet parameters)
    {
        Vector4 neutral = parameters.Color("neutral");
        bool grey = MathF.Abs(neutral.X - neutral.Y) < 1e-5f && MathF.Abs(neutral.Y - neutral.Z) < 1e-5f;
        return grey || parameters.Float("amount") == 0;
    }
}
