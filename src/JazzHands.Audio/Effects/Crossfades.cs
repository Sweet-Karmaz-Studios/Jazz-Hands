using JazzHands.Core.Effects;

namespace JazzHands.Audio.Effects;

/// <summary>The shape of a crossfade's two gains.</summary>
public enum CrossfadeCurve
{
    /// <summary>
    /// Sine and cosine: the two powers add to one all the way across, so two unrelated sounds
    /// keep their level through the middle. The default, as in every editor.
    /// </summary>
    EqualPower,

    /// <summary>Straight lines: the two gains add to one, right for two takes of the same sound, which dip with equal power's bump otherwise.</summary>
    Linear,
}

/// <summary>An equal power crossfade: the default between two sounds.</summary>
[AudioTransition("transition.audio.equal-power", Name = "Equal power crossfade", Category = "Crossfade", Description = "Fades one sound out and the next in with sine and cosine gains, so the level holds through the middle. The usual choice.")]
public sealed class EqualPowerCrossfade;

/// <summary>A linear crossfade, for two takes of the same sound.</summary>
[AudioTransition("transition.audio.linear", Name = "Linear crossfade", Category = "Crossfade", Description = "Fades with straight lines whose gains add to one: right between two takes of the same sound, where equal power would swell in the middle.")]
public sealed class LinearCrossfade;

/// <summary>The crossfade types and their curves.</summary>
public static class Crossfades
{
    /// <summary>The curve a sound transition type makes; equal power for any other.</summary>
    public static CrossfadeCurve CurveOf(string? typeId) =>
        string.Equals(typeId, "transition.audio.linear", StringComparison.Ordinal) ? CrossfadeCurve.Linear : CrossfadeCurve.EqualPower;

    /// <summary>The gain of the incoming sound at a progress from 0 to 1; the outgoing one's is the same at one minus it.</summary>
    public static float Gain(CrossfadeCurve curve, float progress)
    {
        float x = Math.Clamp(progress, 0.0f, 1.0f);
        return curve == CrossfadeCurve.Linear ? x : MathF.Sin(x * MathF.PI * 0.5f);
    }
}
