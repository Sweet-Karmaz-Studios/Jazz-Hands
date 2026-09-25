using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Overlay;

/// <summary>Rounded corners, a border and a shadow on a layer: a picture in picture, a facecam.</summary>
/// <remarks>
/// Worked out from where the layer's picture is placed, so the corners stay round and the border
/// stays even as it moves and scales; sizes are sequence pixels on screen. The shadow is the
/// rounded shape moved and softened, under the picture.
/// </remarks>
[VideoEffect("video.frame", Name = "Frame", Category = "Overlay", Description = "Rounds a layer's corners and gives it a border and a soft drop shadow, following it as it moves and scales: a facecam in the corner, a picture in picture, a card.")]
[Param("corner", ParamType.Float, Default = "24", Min = 0, Max = 2000, SliderMax = 200, Unit = "px", Description = "The corner radius, in sequence pixels on screen.")]
[Param("border", ParamType.Float, Default = "6", Min = 0, Max = 200, SliderMax = 40, Unit = "px", Description = "The border's width, inside the edge; 0 for none.")]
[Param("border-colour", ParamType.Color, Default = "#FFFFFF", Description = "The border's colour.")]
[Param("shadow", ParamType.Float, Default = "0.5", Min = 0, Max = 1, Description = "How dark the shadow is; 0 for none.")]
[Param("shadow-colour", ParamType.Color, Default = "#000000", Description = "The shadow's colour.")]
[Param("angle", ParamType.Float, Default = "135", Min = -36000, Max = 36000, SliderMax = 360, Unit = "deg", Description = "Which way the shadow falls, clockwise from pointing right; 135 is down and left.")]
[Param("distance", ParamType.Float, Default = "14", Min = 0, Max = 2000, SliderMax = 100, Unit = "px", Description = "How far the shadow falls.")]
[Param("softness", ParamType.Float, Default = "30", Min = 0, Max = 1000, SliderMax = 200, Unit = "px", Description = "How soft the shadow's edge is.")]
public sealed class FrameEffect : VideoEffect
{
    private static readonly PassDescriptor Pass = new("Frame.hlsl", "PsFrame");

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; } = [Pass];

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (!Matrix3x2.Invert(context.Placement, out Matrix3x2 inverse))
        {
            context.Copy(input, output);
            return;
        }

        float scale = context.QualityScale;
        Matrix3x2 placement = context.Placement;
        float texelsPerPixel = MathF.Sqrt(MathF.Abs(placement.GetDeterminant()));
        Vector2 size = context.PictureSize;
        Vector4 crop = context.PictureCrop;
        float radians = parameters.Float("angle") * MathF.PI / 180;
        float distance = parameters.Float("distance") * scale;

        var constants = new FrameConstants
        {
            Inverse = new Vector4(inverse.M11, inverse.M12, inverse.M21, inverse.M22),
            Offset = new Vector4(inverse.M31, inverse.M32, texelsPerPixel, parameters.Float("corner") * scale),
            Rect = new Vector4(crop.X * size.X, crop.Y * size.Y, crop.Z * size.X, crop.W * size.Y),
            Border = parameters.Color("border-colour"),
            Shadow = parameters.Color("shadow-colour") * parameters.Float("shadow"),
            Sizes = new Vector4(parameters.Float("border") * scale, MathF.Cos(radians) * distance, MathF.Sin(radians) * distance, parameters.Float("softness") * scale),
        };
        context.Draw(Pass, output, in constants, input);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public Vector4 Inverse;
        public Vector4 Offset;
        public Vector4 Rect;
        public Vector4 Border;
        public Vector4 Shadow;
        public Vector4 Sizes;
    }
}
