using System.Collections.Immutable;
using JazzHands.Core.Effects;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Looks;

/// <summary>Trails: the clip's last frames blended under the current one, fading with age.</summary>
/// <remarks>
/// Not a pass over the picture: it needs earlier frames of the clip, so the render graph builder
/// draws the clip at each earlier frame and averages them, weighted by the decay, the way motion
/// blur averages moments of the shutter (<see cref="MotionBlurLayerSource"/>), and leaves this
/// out of the chain. A clip with echo is not also motion blurred. Frames from before the clip's
/// start are not there, so the first few frames echo less.
/// </remarks>
[VideoEffect(EchoEffect.TypeId, Name = "Echo", Category = "Looks", FramesBefore = 16, Description = "Blends the clip's last few frames under the current one, each fainter than the one after it: trails behind fast action, a dreamy smear on a dash or a hit.")]
[Param(EchoEffect.Count, ParamType.Int, Default = "6", Min = 2, Max = 16, SliderMax = 12, Animatable = false, Description = "How many frames are blended, the current one included.")]
[Param(EchoEffect.Spacing, ParamType.Int, Default = "1", Min = 1, Max = 30, SliderMax = 8, Animatable = false, Description = "Frames between one echo and the next; more spreads the trail further.")]
[Param(EchoEffect.Decay, ParamType.Float, Default = "0.6", Min = 0, Max = 1, Description = "How much of each echo's weight the next older one keeps; lower is a shorter trail.")]
public sealed class EchoEffect : VideoEffect
{
    /// <summary>The effect's type.</summary>
    public const string TypeId = "video.echo";

    /// <summary>The frame count parameter.</summary>
    public const string Count = "frames";

    /// <summary>The spacing parameter.</summary>
    public const string Spacing = "spacing";

    /// <summary>The decay parameter.</summary>
    public const string Decay = "decay";

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes => [];

    /// <inheritdoc />
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Copy(input, output);
    }

    /// <summary>
    /// The weight of each frame, the current one first, summing to one: each older one the decay
    /// times the one after it.
    /// </summary>
    public static float[] Weights(int count, float decay)
    {
        var weights = new float[Math.Max(count, 1)];
        float weight = 1;
        for (int index = 0; index < weights.Length; index++)
        {
            weights[index] = weight;
            weight *= decay;
        }

        float sum = weights.Sum();
        for (int index = 0; index < weights.Length; index++)
        {
            weights[index] /= sum;
        }

        return weights;
    }
}
