using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JazzHands.Render.Effects.Keying;

/// <summary>
/// Removes the background behind a person, without a green screen (Phase 43): the person's matte
/// from Robust Video Matting, made ahead for the clip's file, with the keyers' choke, feather and
/// matte view.
/// </summary>
/// <remarks>
/// The network does not run while drawing: <c>clip.remove-background</c> makes the file's matte
/// once, and the render graph builder hands each frame's matte to <see cref="PersonMatteRunner"/>
/// in this effect's place. Without a matte yet the picture passes through unchanged.
/// </remarks>
[VideoEffect(TypeId, Name = "Remove background", Category = "AI", Description = "Cuts a person out of the picture without a green screen, using a matte made on this computer; choke, feather and view the matte as a keyer does.")]
[Param("choke", ParamType.Float, Default = "0", Min = -50, Max = 50, SliderMax = 10, Unit = "px", Description = "Shrinks the kept area by this many sequence pixels; negative grows it.")]
[Param("feather", ParamType.Float, Default = "0", Min = 0, Max = 100, SliderMax = 20, Unit = "px", Description = "Softens the edge of what is kept, in sequence pixels.")]
[Param("invert", ParamType.Bool, Default = "false", Animatable = false, Description = "Keep the background and remove the person.")]
[Param("view", ParamType.Enum, Default = "result", Choices = "result, matte", Animatable = false, Description = "The cut out picture, or the matte in black and white.")]
public sealed class PersonMatteEffect : VideoEffect
{
    /// <summary>The effect's type id.</summary>
    public const string TypeId = "video.matte.person";

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; } = [PersonMatteRunner.Cut, .. ChromaKeyEffect.MattePasses];

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        // No matte has been made for this clip's file yet: nothing is removed.
        context.Copy(input, output);
    }
}

/// <summary>
/// Runs <see cref="PersonMatteEffect"/> with one frame's matte: the matte uploaded, mapped from the
/// clip's own pixels to the frame, and then choked, feathered and applied as the chroma key does.
/// </summary>
/// <remarks>
/// The upload is the named exception's other half: the matte was made on the CPU from a network
/// and comes up once a frame, a few hundred kilobytes at 720p.
/// </remarks>
/// <param name="matte">The frame's matte.</param>
/// <param name="parameters">The effect's parameters at this frame.</param>
public sealed class PersonMatteRunner(MatteFrame matte, ParameterSet parameters) : ILayerEffect
{
    /// <summary>The pass that maps the matte onto the frame and cuts the picture by it.</summary>
    internal static readonly PassDescriptor Cut = new("Keying.hlsl", "PsPersonMatte");

    /// <summary>The frame's matte.</summary>
    public MatteFrame Matte { get; } = matte;

    /// <inheritdoc />
    public unsafe void Apply(EffectContext context, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        RenderTarget alpha = context.Rent(Matte.Width, Matte.Height, Format.R8_UNorm);
        fixed (byte* bytes = Matte.Alpha)
        {
            context.Device.ImmediateContext.UpdateSubresource(alpha.Texture, 0, null, (nint)bytes, (uint)Matte.Width, (uint)Matte.Alpha.Length);
        }

        if (!Matrix3x2.Invert(context.Placement, out Matrix3x2 inverse))
        {
            context.Return(alpha);
            context.Clear(output, Vector4.Zero);
            return;
        }

        var cut = new EffectValues
        {
            Values = new Vector4(inverse.M11, inverse.M12, inverse.M21, inverse.M22),
            More = new Vector4(inverse.M31, inverse.M32, 1.0f / context.PictureSize.X, 1.0f / context.PictureSize.Y),
            FlagX = parameters.Bool("invert") ? 1u : 0u,
        };
        RenderTarget keyed = context.Rent(input.Width, input.Height);
        context.Draw(Cut, keyed, in cut, input, alpha);
        context.Return(alpha);

        ChromaKeyEffect.Finish(context, keyed, output, parameters.Float("choke"), parameters.Float("feather"), parameters.Enum("view") == "matte");
    }
}
