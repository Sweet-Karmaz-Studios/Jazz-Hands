using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Color;

/// <summary>
/// A grade built as a graph of nodes (Phase 44). The nodes are held by the effect in the project
/// (<see cref="Effect.Graph"/>) and drawn by the compositor, each by its own colour effect, into
/// the steps of <see cref="EffectNode.Grade"/>; this class draws the joins between them.
/// </summary>
[VideoEffect(GradeGraph.TypeId, Name = "Node graph", Category = "Color", Description = "A grade built as nodes, the way a colourist builds one: corrections one after another or side by side and mixed, each limited by its own masks or a qualifier's key. Build it in the Colour panel's node view, or with the color node commands.")]
public sealed class ColorGraphEffect : VideoEffect
{
    private static readonly PassDescriptor MixPass = new("ColorGraph.hlsl", "PsMix");
    private static readonly PassDescriptor KeyPass = new("ColorGraph.hlsl", "PsKey");

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; } = [MixPass, KeyPass];

    /// <summary>With no nodes to draw, a graph shows its picture unchanged.</summary>
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Copy(input, output);
    }

    /// <summary>Mixes up to four pictures by their shares, which add up to one.</summary>
    internal static void Mix(EffectContext context, ReadOnlySpan<RenderTarget> inputs, ReadOnlySpan<float> shares, RenderTarget output)
    {
        // Every slot is bound, to the first picture where there is no other, at a share of
        // nothing: a slot left as the last draw bound it could hold anything, even a NaN.
        Span<float> share = stackalloc float[GradeGraph.MostInputs];
        var bound = new RenderTarget[GradeGraph.MostInputs];
        for (int slot = 0; slot < GradeGraph.MostInputs; slot++)
        {
            bound[slot] = slot < inputs.Length ? inputs[slot] : inputs[0];
            share[slot] = slot < inputs.Length && slot < shares.Length ? shares[slot] : 0;
        }

        var values = new EffectValues { Values = new Vector4(share[0], share[1], share[2], share[3]) };
        context.Draw(MixPass, output, in values, bound);
    }

    /// <summary>A correction where a qualifier's matte is white, and its input where it is black.</summary>
    internal static void Key(EffectContext context, RenderTarget input, RenderTarget corrected, RenderTarget matte, RenderTarget output)
    {
        var values = default(EffectValues);
        context.Draw(KeyPass, output, in values, input, corrected, matte);
    }
}
