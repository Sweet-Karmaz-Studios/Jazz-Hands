using JazzHands.Core.Effects;

namespace JazzHands.Audio.Effects;

/// <summary>
/// Turns a track down while another track has sound: music under a voice.
/// </summary>
/// <remarks>
/// <para>
/// It listens to its key track's sound after that track's fader (so muting the voice lets the music
/// back up): the loudest channel's peak, followed with an instant attack and a 20 ms release. While
/// that is over the threshold, and for the hold time after it falls under, the track is turned
/// down by the depth; the turn down and the return follow the attack and release times, in
/// decibels. The hold bridges the gaps between words and syllables, so the music sits at one level
/// under a sentence instead of pumping with each word; a pause longer than the hold brings it back.
/// </para>
/// <para>
/// A track effect: <c>audio.duck</c> puts it on the music track with the voice track as its key. The
/// mixer mixes a key track before the tracks listening to it, so the ducking has no delay.
/// </para>
/// </remarks>
[AudioEffect("audio.ducker", Name = "Ducker", Category = "Dynamics", Description = "Turns this track down while another track has sound, such as music under a voice. The key names the other track; audio.duck sets it up.")]
[Param("key", ParamType.Text, Default = "", Animatable = false, Description = "The track that turns this one down, by its id or its name.")]
[Param("depth", ParamType.Float, Default = "-12", Min = -60, Max = 0, Unit = "dB", Description = "How far down it goes while the key has sound.")]
[Param("threshold", ParamType.Float, Default = "-40", Min = -80, Max = 0, Unit = "dB", Description = "How loud the key must be to duck.")]
[Param("attack", ParamType.Float, Default = "15", Min = 0.1, Max = 1000, SliderMax = 200, Unit = "ms", Description = "How quickly it goes down when the key starts.")]
[Param("hold", ParamType.Float, Default = "300", Min = 0, Max = 5000, SliderMax = 1000, Unit = "ms", Description = "How long it stays down after the key stops, to ride over the gaps between words.")]
[Param("release", ParamType.Float, Default = "400", Min = 1, Max = 10000, SliderMax = 2000, Unit = "ms", Description = "How quickly it comes back up.")]
public sealed class DuckerEffect : AudioEffect, ITrackEffect
{
    private const int Depth = 1;
    private const int Threshold = 2;
    private const int Attack = 3;
    private const int Hold = 4;
    private const int Release = 5;

    private const float DetectorReleaseMs = 20.0f;

    private float _envelope;
    private long _holding;
    private float _gainDb;

    /// <summary>How far it is turned down at the end of the last block, in dB (0 or less), for a meter.</summary>
    public float ReductionDb => _gainDb;

    /// <inheritdoc />
    public override void Process(in AudioEffectBlock block)
    {
        int frames = block.Frames;
        int channels = block.Channels;
        int rate = block.SampleRate;
        AudioBuffer? key = block.Key;
        int keyChannels = key is null ? 0 : Math.Min(key.Channels, channels);
        float detector = 1.0f - CompressorEffect.Coefficient(DetectorReleaseMs, rate);
        float attack = CompressorEffect.Coefficient(block.To[Attack], rate);
        float release = CompressorEffect.Coefficient(block.To[Release], rate);
        long hold = (long)(block.To[Hold] * 0.001f * rate);

        for (int index = 0; index < frames; index++)
        {
            float t = (float)index / frames;
            float depth = CompressorEffect.Lerp(block.From[Depth], block.To[Depth], t);
            float threshold = Dsp.DbToGain(CompressorEffect.Lerp(block.From[Threshold], block.To[Threshold], t));

            float peak = 0.0f;
            for (int channel = 0; channel < keyChannels; channel++)
            {
                peak = Math.Max(peak, Math.Abs(key!.Plane(channel)[index]));
            }

            _envelope = peak >= _envelope ? peak : peak + ((_envelope - peak) * detector);
            if (_envelope >= threshold)
            {
                _holding = hold;
            }
            else if (_holding > 0)
            {
                _holding--;
            }

            float wanted = _envelope >= threshold || _holding > 0 ? depth : 0.0f;
            _gainDb += (wanted - _gainDb) * (wanted < _gainDb ? attack : release);
            float gain = Dsp.DbToGain(_gainDb);

            for (int channel = 0; channel < channels; channel++)
            {
                block.Plane(channel)[index] *= gain;
            }
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _envelope = 0.0f;
        _holding = 0;
        _gainDb = 0.0f;
    }
}
