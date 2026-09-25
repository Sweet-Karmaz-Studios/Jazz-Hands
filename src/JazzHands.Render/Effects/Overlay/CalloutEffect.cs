using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Overlay;

/// <summary>A magnifier: a region of the picture shown bigger in a rounded, bordered inset.</summary>
/// <remarks>
/// The region is a square around <c>target</c>, which is what <c>tracking.apply</c> moves, so a
/// callout can follow something small across the frame. A ring marks the region and a line joins
/// it to the inset; both go when <c>border</c> is 0.
/// </remarks>
[VideoEffect("video.callout", Name = "Callout", Category = "Overlay", Description = "Shows a region of the picture magnified in a rounded, bordered inset, with a ring round the region and a line to it: pointing at something small.")]
[Param("target", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The middle of the region shown, in sequence pixels from the frame centre. tracking.apply moves it.")]
[Param("region", ParamType.Float, Default = "120", Min = 4, Max = 4000, SliderMax = 600, Unit = "px", Description = "The side of the square region shown, in sequence pixels.")]
[Param("zoom", ParamType.Float, Default = "2.5", Min = 1, Max = 16, SliderMax = 6, Description = "How much bigger the inset shows it.")]
[Param("position", ParamType.Point, Default = "520, -240", Unit = "px", Description = "The middle of the inset, in sequence pixels from the frame centre.")]
[Param("corner", ParamType.Float, Default = "16", Min = 0, Max = 2000, SliderMax = 100, Unit = "px", Description = "The inset's corner radius.")]
[Param("border", ParamType.Float, Default = "4", Min = 0, Max = 100, SliderMax = 20, Unit = "px", Description = "The width of the inset's border; the ring and line are half as wide. 0 draws none of them.")]
[Param("colour", ParamType.Color, Default = "#FFFFFF", Description = "The border, ring and line.")]
[Param("line", ParamType.Bool, Default = "true", Animatable = false, Description = "Join the region to the inset with a line.")]
[Param("ring", ParamType.Bool, Default = "true", Animatable = false, Description = "Mark the region with a ring.")]
public sealed class CalloutEffect() : SinglePassEffect("Callout.hlsl", "PsCallout")
{
    /// <inheritdoc />
    protected override EffectValues Values(EffectContext context, ParameterSet parameters, RenderTarget input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);

        float scale = context.QualityScale;
        var middle = new Vector2(input.Width, input.Height) / 2;
        Vector2 target = middle + (parameters.Float2("target") * scale);
        Vector2 inset = middle + (parameters.Float2("position") * scale);
        return new()
        {
            Values = new Vector4(target, inset.X, inset.Y),
            More = new Vector4(parameters.Float("region") * scale / 2, parameters.Float("zoom"), parameters.Float("corner") * scale, parameters.Float("border") * scale),
            FlagX = Flag(parameters.Bool("line")),
            FlagY = Flag(parameters.Bool("ring")),
            Extra = parameters.Color("colour"),
        };
    }
}
