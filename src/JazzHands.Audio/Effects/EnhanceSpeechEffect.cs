using JazzHands.Core.Effects;
using JazzHands.Core.Model;

namespace JazzHands.Audio.Effects;

/// <summary>
/// Takes noise, room and hum out of speech with DeepFilterNet 3 (Phase 43), made ahead on this
/// computer: Premiere's Enhance Speech, Resolve's Voice Isolation.
/// </summary>
/// <remarks>
/// The network hears the whole recording once (<c>audio.enhance-speech</c> does it, and adding
/// this effect does too) and the result is kept in the cache. While mixing, the enhanced sound is
/// read in place of the clip's own, mixed with it by <c>amount</c> (see
/// <see cref="AudioSourceRef.Enhance"/>); this class itself passes its block through. Before the
/// enhanced sound is made the clip is heard as it was.
/// </remarks>
[AudioEffect(TypeId, Name = "Enhance speech", Category = "AI", Description = "Takes background noise, room and hum out of speech with a network that runs on this computer; amount mixes it with the original.")]
[Param("amount", ParamType.Float, Default = "100", Min = 0, Max = 100, Unit = "%", Animatable = false, Description = "How much of the enhanced speech is heard: 100 all of it, lower mixes the original back in.")]
public sealed class EnhanceSpeechEffect : AudioEffect, IClipEffect
{
    /// <summary>The effect's type id.</summary>
    public const string TypeId = "audio.enhance-speech";

    /// <summary>An effect's amount, 0 to 1.</summary>
    public static float Amount(Effect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return effect.Parameters.FirstOrDefault(parameter => parameter.Name == "amount")?.Value is StaticValue { Value: ParamValue.Float { Value: var percent } }
            ? Math.Clamp(percent / 100f, 0f, 1f)
            : 1f;
    }

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        // The enhanced sound comes in through the clip's source; nothing to do here.
    }
}
