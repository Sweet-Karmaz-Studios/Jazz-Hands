using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Keying;

/// <summary>
/// Keys out a colour, a green or blue screen, with spill suppression, choke and feather.
/// </summary>
/// <remarks>
/// The distance from the key colour is measured in the CbCr plane of BT.709 Y'CbCr on perceptual
/// colour, so a shadowed and a lit part of the screen key alike. Tolerance is how close a colour
/// has to be to go completely; softness is how far beyond that it fades back in. Spill takes the
/// key's hue out of what is kept. Choke shrinks the matte (positive) or grows it (negative) before
/// the feather softens it. The matte view shows kept as white and keyed as black.
/// </remarks>
[VideoEffect("video.key.chroma", Name = "Chroma key", Category = "Keying", Description = "Removes a green or blue screen (any key colour), with spill suppression, choke and feather; view the matte to check it.")]
[Param("key-colour", ParamType.Color, Default = "#00FF00", Description = "The colour to remove: the screen's colour.")]
[Param("tolerance", ParamType.Float, Default = "0.15", Min = 0, Max = 1, SliderMax = 0.6, Description = "How close to the key colour a colour must be to go completely.")]
[Param("softness", ParamType.Float, Default = "0.1", Min = 0, Max = 1, SliderMax = 0.4, Description = "How far past the tolerance colours fade back in.")]
[Param("spill", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How much of the key colour's tint is taken out of what is kept.")]
[Param("choke", ParamType.Float, Default = "0", Min = -50, Max = 50, SliderMax = 10, Unit = "px", Description = "Shrinks the kept area by this many sequence pixels; negative grows it.")]
[Param("feather", ParamType.Float, Default = "0", Min = 0, Max = 100, SliderMax = 20, Unit = "px", Description = "Softens the edge of what is kept, in sequence pixels.")]
[Param("view", ParamType.Enum, Default = "result", Choices = "result, matte", Animatable = false, Description = "The keyed picture, or the matte in black and white.")]
public sealed class ChromaKeyEffect : VideoEffect
{
    private static readonly PassDescriptor Key = new("Keying.hlsl", "PsKey");
    private static readonly PassDescriptor Morph = new("Keying.hlsl", "PsMorph");
    private static readonly PassDescriptor Feather = new("Keying.hlsl", "PsFeather");
    private static readonly PassDescriptor FinishPass = new("Keying.hlsl", "PsFinish");

    /// <summary>The passes that choke, feather and finish a matte, which the person matte shares.</summary>
    internal static ImmutableArray<PassDescriptor> MattePasses { get; } = [Morph, Feather, FinishPass];

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; } = [Key, Morph, Feather, FinishPass];

    /// <summary>A linear premultiplied colour as the key shader's perceptual CbCr.</summary>
    public static Vector2 KeyChroma(Vector4 linear)
    {
        Vector4 straight = linear.W > 0 ? linear / linear.W : Vector4.Zero;
        float r = Srgb(straight.X);
        float g = Srgb(straight.Y);
        float b = Srgb(straight.Z);
        float y = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
        return new Vector2((b - y) / 1.8556f, (r - y) / 1.5748f);
    }

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        Vector2 chroma = KeyChroma(parameters.Color("key-colour"));
        RenderTarget matte = context.Rent(input.Width, input.Height);
        var key = new EffectValues
        {
            Values = new Vector4(chroma, parameters.Float("tolerance"), parameters.Float("softness")),
            More = new Vector4(parameters.Float("spill"), 0, 0, 0),
        };
        context.Draw(Key, matte, in key, input);
        Finish(context, matte, output, parameters.Float("choke"), parameters.Float("feather"), parameters.Enum("view") == "matte");
    }

    /// <summary>
    /// Chokes and feathers a keyed picture's matte (straight perceptual colour, the matte in alpha)
    /// and writes the result, or the matte itself, as premultiplied linear light. Hands back
    /// <paramref name="keyed"/>.
    /// </summary>
    internal static void Finish(EffectContext context, RenderTarget keyed, RenderTarget output, float chokePixels, float featherPixels, bool viewMatte)
    {
        RenderTarget matte = keyed;
        float choke = chokePixels * context.QualityScale;
        int taps = (int)MathF.Round(MathF.Abs(choke));
        if (taps > 0)
        {
            uint grow = choke < 0 ? 1u : 0u;
            matte = Separable(context, matte, Morph, (uint)taps, grow, 0.0f);
        }

        float sigma = featherPixels * context.QualityScale / 2.0f;
        if (sigma > 0.25f)
        {
            matte = Separable(context, matte, Feather, (uint)Math.Min(64, (int)MathF.Ceiling(sigma * 3.0f)), 0, sigma);
        }

        var finish = new EffectValues { FlagX = viewMatte ? 1u : 0u };
        context.Draw(FinishPass, output, in finish, matte);
        context.Return(matte);
    }

    /// <summary>A pass across then down, returning the second target and handing back the first and the input.</summary>
    private static RenderTarget Separable(EffectContext context, RenderTarget from, PassDescriptor pass, uint taps, uint mode, float sigma)
    {
        RenderTarget across = context.Rent(from.Width, from.Height);
        RenderTarget down = context.Rent(from.Width, from.Height);

        var horizontal = new EffectValues { Values = new Vector4(1.0f / from.Width, 0, sigma, 0), FlagX = taps, FlagY = mode };
        context.Draw(pass, across, in horizontal, from);
        var vertical = new EffectValues { Values = new Vector4(0, 1.0f / from.Height, sigma, 0), FlagX = taps, FlagY = mode };
        context.Draw(pass, down, in vertical, across);

        context.Return(across);
        context.Return(from);
        return down;
    }

    private static float Srgb(float linear) => ParamValues.LinearToSrgb(Math.Clamp(linear, 0.0f, 1.0f));
}

/// <summary>Keys out the dark (or the bright) parts of the picture.</summary>
[VideoEffect("video.key.luma", Name = "Luma key", Category = "Keying", Description = "Removes the dark parts of the picture, or the bright ones, for fire, smoke or text shot on black or white.")]
[Param("threshold", ParamType.Float, Default = "0.1", Min = 0, Max = 1, Description = "Brightness below which the picture goes.")]
[Param("softness", ParamType.Float, Default = "0.1", Min = 0, Max = 1, Description = "How gradually it fades in above the threshold.")]
[Param("key-out", ParamType.Enum, Default = "dark", Choices = "dark, bright", Animatable = false, Description = "Remove the dark parts or the bright ones.")]
[Param("view", ParamType.Enum, Default = "result", Choices = "result, matte", Animatable = false, Description = "The keyed picture, or the matte in black and white.")]
public sealed class LumaKeyEffect() : SinglePassEffect("Keying.hlsl", "PsLuma")
{
    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input) =>
        new()
        {
            Values = new Vector4(parameters.Float("threshold"), parameters.Float("softness"), 0, 0),
            FlagX = Flag(parameters.Enum("key-out") == "bright"),
            FlagY = Flag(parameters.Enum("view") == "matte"),
        };
}
