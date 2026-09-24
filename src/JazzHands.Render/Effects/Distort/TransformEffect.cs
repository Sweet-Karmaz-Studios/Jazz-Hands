using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Distort;

/// <summary>
/// Moves, scales and turns the picture as it stands at this point in the chain.
/// </summary>
/// <remarks>
/// A clip's own transform places its source in the frame before any effect runs. This one runs
/// in the chain, on the placed picture, so it can come after a blur, be stacked for a shake on
/// top of a move, or sit on an adjustment layer to move everything underneath. Position and
/// anchor are sequence pixels from the frame centre.
/// </remarks>
[VideoEffect("video.transform", Name = "Transform", Category = "Distort", Description = "Moves, scales and turns the picture as it stands at this point in the effect chain.")]
[Param("position", ParamType.Point, Default = "0, 0", Unit = "px", Description = "Offset from where the picture is, in sequence pixels.")]
[Param("scale", ParamType.Float2, Default = "1, 1", SliderMax = 4, Description = "Multiplier per axis; a negative flips.")]
[Param("rotation", ParamType.Float, Default = "0", Min = -36000, Max = 36000, SliderMax = 360, Unit = "deg", Description = "Degrees clockwise.")]
[Param("anchor", ParamType.Point, Default = "0, 0", Unit = "px", Description = "The pivot, in sequence pixels from the frame centre.")]
[Param("opacity", ParamType.Float, Default = "1", Min = 0, Max = 1, Description = "0 is invisible, 1 as it was.")]
public sealed class TransformEffect : VideoEffect
{
    private static readonly PassDescriptor Quad = new("VideoTransform.hlsl", "PsQuad", "VsQuad", Vertices: 4);

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; } = [Quad];

    /// <summary>The matrix from input texels to output texels, for a frame of a given size in texels.</summary>
    public static Matrix3x2 MatrixFor(ParameterSet parameters, Vector2 frame, float qualityScale)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        Vector2 pivot = (frame / 2.0f) + (parameters.Float2("anchor") * qualityScale);
        return Matrix3x2.CreateTranslation(-pivot)
            * Matrix3x2.CreateScale(parameters.Float2("scale"))
            * Matrix3x2.CreateRotation(parameters.Float("rotation") * MathF.PI / 180.0f)
            * Matrix3x2.CreateTranslation(pivot + (parameters.Float2("position") * qualityScale));
    }

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        Matrix3x2 m = MatrixFor(parameters, new Vector2(input.Width, input.Height), context.QualityScale);
        float opacity = parameters.Float("opacity");

        if (m.IsIdentity && opacity >= 1.0f)
        {
            context.Copy(input, output);
            return;
        }

        var constants = new TransformConstants
        {
            MatrixRow0 = new Vector4(m.M11, m.M21, m.M31, 0.0f),
            MatrixRow1 = new Vector4(m.M12, m.M22, m.M32, 0.0f),
            Opacity = opacity,
            Bicubic = 1u,
        };

        context.Clear(output, Vector4.Zero);
        context.Draw(Quad, output, in constants, input);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TransformConstants
    {
        public Vector4 MatrixRow0;
        public Vector4 MatrixRow1;
        public float Opacity;
        public uint Bicubic;
        public Vector2 Padding;
    }
}
