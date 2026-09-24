using JazzHands.Core.Effects;

namespace JazzHands.Render.Effects.Generators;

/// <summary>
/// A flat colour filling the frame. The compositor draws it as a one texel source stretched over
/// the frame; this class is its description, so its colour is in the inspector and keyframes
/// like any other parameter. The parameters are stored on the clip as an effect of this type.
/// </summary>
[Generator("gen.solid", Name = "Solid Colour", Category = "Generators", Description = "A flat colour filling the frame.")]
[Param("color", ParamType.Color, Default = "rgba(0.5, 0.5, 0.5, 1)", Description = "The colour; typed as sRGB hex, stored linear.")]
public static class SolidGenerator
{
}
