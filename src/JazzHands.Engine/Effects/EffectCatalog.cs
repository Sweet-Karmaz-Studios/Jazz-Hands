using JazzHands.Audio.Effects;
using JazzHands.Core.Effects;
using JazzHands.Render.Effects;

namespace JazzHands.Engine.Effects;

/// <summary>
/// Every effect type the editor has: the render layer's picture effects, generators and
/// transitions and the audio layer's sound effects and crossfades. The engine is the one place
/// that sees both.
/// </summary>
/// <remarks>
/// The picture side can change while the editor runs, when a transition written outside the
/// build is added or edited (<see cref="Render.Effects.Transitions.CustomTransitions"/>), so the
/// joined registry is rebuilt when it does and read fresh each time.
/// </remarks>
public static class EffectCatalog
{
    private static Joined _joined = new(VideoEffects.Registry);

    /// <summary>The joined registry the commands, queries and front ends read.</summary>
    public static EffectRegistry Registry
    {
        get
        {
            EffectRegistry video = VideoEffects.Registry;
            Joined joined = Volatile.Read(ref _joined);
            if (!ReferenceEquals(joined.Video, video))
            {
                joined = new Joined(video);
                Volatile.Write(ref _joined, joined);
            }

            return joined.All;
        }
    }

    /// <summary>The picture registry a join was made from, and the join.</summary>
    private sealed class Joined(EffectRegistry video)
    {
        public EffectRegistry Video { get; } = video;

        public EffectRegistry All { get; } = EffectRegistry.Combine(video, AudioEffects.Registry);
    }
}
