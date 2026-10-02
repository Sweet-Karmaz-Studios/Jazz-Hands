using System.Collections.Immutable;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Render.Compositing;

namespace JazzHands.Render.Effects.Compositing;

/// <summary>
/// A compositing node graph inside a clip (Phase 49). The nodes are held by the effect in the
/// project (<see cref="Effect.Comp"/>) and drawn by the compositor from the steps of
/// <see cref="EffectNode.Comp"/>; this class draws the matte join between them.
/// </summary>
[VideoEffect(CompGraph.TypeId, Name = "Comp graph", Category = "Compositing", Description = "A compositing node graph: the clip's picture through merges, transforms, mattes, effects, other media and 3D renders. Build it in the Nodes panel, or with the comp commands.")]
public sealed class CompGraphEffect : VideoEffect
{
    private static readonly PassDescriptor MattePass = new("Comp.hlsl", "PsMatte");

    /// <inheritdoc />
    public override ImmutableArray<PassDescriptor> Passes { get; } = [MattePass];

    /// <summary>With no nodes to draw, a graph shows its picture unchanged.</summary>
    public override void Apply(EffectContext context, ParameterSet parameters, RenderTarget input, RenderTarget output)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Copy(input, output);
    }

    /// <summary>A picture kept where a matte's alpha or brightness says: mode 0 alpha, 1 luma, 2 and 3 those inverted.</summary>
    internal static void Matte(EffectContext context, RenderTarget input, RenderTarget matte, uint mode, RenderTarget output)
    {
        var values = new EffectValues { FlagX = mode };
        context.Draw(MattePass, output, in values, input, matte);
    }
}
