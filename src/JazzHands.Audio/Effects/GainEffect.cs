using JazzHands.Core.Effects;

namespace JazzHands.Audio.Effects;

/// <summary>
/// Turns the sound up or down, ramped across each block so an automated gain does not step.
/// </summary>
/// <remarks>
/// The clip's own volume does this too; a gain effect is for a change at a particular point in
/// the chain, such as before a compressor, which is what Phase 20's effects need.
/// </remarks>
[AudioEffect("audio.gain", Name = "Gain", Category = "Volume", Description = "Turns the sound up or down, in decibels.")]
[Param("gain", ParamType.Float, Default = "0", Min = -144, Max = 24, SliderMax = 12, Unit = "dB", Description = "Gain in decibels; -144 is silence.")]
public sealed class GainEffect : AudioEffect
{
    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        float from = Dsp.DbToGain(block.From[0]);
        float to = Dsp.DbToGain(block.To[0]);

        if (from == 1.0f && to == 1.0f)
        {
            return;
        }

        int frames = block.Frames;
        for (int channel = 0; channel < block.Channels; channel++)
        {
            Span<float> samples = block.Plane(channel);

            if (from == to)
            {
                for (int index = 0; index < frames; index++)
                {
                    samples[index] *= from;
                }

                continue;
            }

            for (int index = 0; index < frames; index++)
            {
                samples[index] *= from + ((to - from) * index / frames);
            }
        }
    }
}
