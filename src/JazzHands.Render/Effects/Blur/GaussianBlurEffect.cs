using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Blur;

/// <summary>
/// A Gaussian blur, separable, in one or both directions.
/// </summary>
/// <remarks>
/// The radius is how far the blur reaches, in sequence pixels: three standard deviations, past
/// which a Gaussian is under half a percent. Beyond <see cref="MaxSigma"/> texels the picture is
/// halved (a two by two box each time) until the kernel fits, blurred there and drawn back up,
/// so a 500 pixel blur at 4K costs about what a 30 pixel one does.
/// </remarks>
[VideoEffect("video.blur.gaussian", Name = "Gaussian Blur", Category = "Blur", Description = "Softens the picture with a Gaussian blur, in both directions or one.")]
[Param("radius", ParamType.Float, Default = "8", Min = 0, Max = 1000, SliderMax = 100, Unit = "px", Description = "How far the blur reaches, in sequence pixels.")]
[Param("direction", ParamType.Enum, Default = "both", Choices = "both, horizontal, vertical", Animatable = false, Description = "Blur both ways, or only across or only down.")]
[Param("repeat-edges", ParamType.Bool, Default = "true", Animatable = false, Description = "Repeat the frame's edge pixels past it rather than blurring in transparency.")]
public sealed class GaussianBlurEffect : VideoEffect
{
    /// <summary>The widest kernel run at full size, in texels; wider ones run on a smaller copy.</summary>
    internal const float MaxSigma = 24.0f;

    private static readonly PassDescriptor BlurPass = new("GaussianBlur.hlsl", "PsBlur");
    private static readonly PassDescriptor Down = new("GaussianBlur.hlsl", "PsDown");
    private static readonly PassDescriptor Up = new("GaussianBlur.hlsl", "PsUp");

    /// <summary>The passes <see cref="Blur"/> draws with, for effects built on it to list.</summary>
    internal static ImmutableArray<PassDescriptor> BlurPasses { get; } = [BlurPass, Down, Up];

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes => BlurPasses;

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        string direction = parameters.Enum("direction");
        Blur(
            context,
            input,
            output,
            parameters.Float("radius") * context.QualityScale / 3.0f,
            across: direction != "vertical",
            down: direction != "horizontal",
            repeat: parameters.Bool("repeat-edges"));
    }

    /// <summary>
    /// A Gaussian of <paramref name="sigma"/> texels from one target into another the same size,
    /// halving past <see cref="MaxSigma"/>. What glow, sharpen and drop shadow blur with.
    /// </summary>
    internal static void Blur(EffectContext context, RenderTarget input, RenderTarget output, float sigma, bool across = true, bool down = true, bool repeat = true)
    {
        if (sigma < 0.1f)
        {
            context.Copy(input, output);
            return;
        }

        // Halve until the kernel fits, but not below a few texels: past that there is nothing left.
        RenderTarget source = input;
        int width = input.Width;
        int height = input.Height;
        while (sigma > MaxSigma && width > 16 && height > 16)
        {
            width = Math.Max(1, (width + 1) / 2);
            height = Math.Max(1, (height + 1) / 2);
            RenderTarget half = context.Rent(width, height);
            context.Draw(Down, half, default(BlurConstants), source);

            if (!ReferenceEquals(source, input))
            {
                context.Return(source);
            }

            source = half;
            sigma /= 2.0f;
        }

        bool scaled = !ReferenceEquals(source, input);
        RenderTarget result = scaled ? context.Rent(width, height) : output;

        if (across && down)
        {
            RenderTarget middle = context.Rent(width, height);
            Pass(context, source, middle, new Vector2(1.0f / width, 0.0f), sigma, repeat);
            Pass(context, middle, result, new Vector2(0.0f, 1.0f / height), sigma, repeat);
            context.Return(middle);
        }
        else
        {
            Pass(context, source, result, across ? new Vector2(1.0f / width, 0.0f) : new Vector2(0.0f, 1.0f / height), sigma, repeat);
        }

        if (scaled)
        {
            context.Draw(Up, output, default(BlurConstants), result);
            context.Return(result);
            context.Return(source);
        }
    }

    private static void Pass(EffectContext context, RenderTarget from, RenderTarget to, Vector2 direction, float sigma, bool repeat)
    {
        var constants = new BlurConstants
        {
            Direction = direction,
            Sigma = sigma,
            Pairs = (int)MathF.Ceiling(sigma * 3.0f / 2.0f),
            RepeatEdges = repeat ? 1u : 0u,
        };

        context.Draw(BlurPass, to, in constants, from);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurConstants
    {
        public Vector2 Direction;
        public float Sigma;
        public int Pairs;
        public uint RepeatEdges;
        public Vector3 Padding;
    }
}
