using JazzHands.Audio.Effects;
using JazzHands.Core.Effects;
using JazzHands.Render.Effects;

namespace JazzHands.Engine.Effects;

/// <summary>
/// Every effect type the editor has: the render layer's picture effects and generators and the
/// audio layer's sound effects. The engine is the one place that sees both.
/// </summary>
public static class EffectCatalog
{
    /// <summary>The joined registry the commands, queries and front ends read.</summary>
    public static EffectRegistry Registry { get; } = EffectRegistry.Combine(VideoEffects.Registry, AudioEffects.Registry);
}
