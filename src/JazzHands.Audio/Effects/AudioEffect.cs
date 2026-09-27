using System.Collections.Immutable;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Audio.Effects;

/// <summary>
/// One block of sound for an effect to change in place, with its parameters at both ends.
/// </summary>
/// <remarks>
/// Parameters are numbers here: a float as itself, a switch as 0 or 1, a whole number as itself
/// and an enum as the index of its choice. <see cref="From"/> is each parameter at the first
/// sample of the block and <see cref="To"/> at the first sample after it, so an effect can ramp
/// across the block instead of stepping every 10 ms.
/// </remarks>
public readonly ref struct AudioEffectBlock
{
    /// <summary>Creates a block.</summary>
    public AudioEffectBlock(AudioBuffer buffer, int offset, int frames, int channels, int sampleRate, ReadOnlySpan<float> from, ReadOnlySpan<float> to, AudioBuffer? key = null, long time = 0)
    {
        Key = key;
        Time = time;
        Buffer = buffer;
        Offset = offset;
        Frames = frames;
        Channels = channels;
        SampleRate = sampleRate;
        From = from;
        To = to;
    }

    /// <summary>The sound, planar.</summary>
    public AudioBuffer Buffer { get; }

    /// <summary>Where the block starts in each plane.</summary>
    public int Offset { get; }

    /// <summary>How many samples.</summary>
    public int Frames { get; }

    /// <summary>How many channels carry sound.</summary>
    public int Channels { get; }

    /// <summary>The rate.</summary>
    public int SampleRate { get; }

    /// <summary>The owner's sample at the block's start, for effects that evaluate their own automation (a plugin's parameters).</summary>
    public long Time { get; }

    /// <summary>Each parameter at the block's first sample, in declared order.</summary>
    public ReadOnlySpan<float> From { get; }

    /// <summary>Each parameter at the sample after the block.</summary>
    public ReadOnlySpan<float> To { get; }

    /// <summary>One channel's samples for the block.</summary>
    public Span<float> Plane(int channel) => Buffer.Plane(channel, Offset, Frames);

    /// <summary>
    /// The key track's sound for the same stretch, from its first sample, for an effect that
    /// listens to another track (a ducker); null when it has none or its key is silent.
    /// </summary>
    public AudioBuffer? Key { get; }
}

/// <summary>
/// The running half of a sound effect.
/// </summary>
/// <remarks>
/// A class with <see cref="AudioEffectAttribute"/> and <see cref="ParamAttribute"/> on it is the
/// description and the implementation. One instance per effect instance per mix, made on the
/// thread that builds the mix and kept across rebuilds while the effect is still there, so its
/// state (a filter's memory, a compressor's envelope) survives an edit elsewhere. Only the audio
/// thread calls <see cref="Process"/>, and it must not allocate, lock or block.
/// </remarks>
public abstract class AudioEffect
{
    /// <summary>
    /// How long it goes on sounding after its input stops, in seconds: a reverb's decay, a
    /// delay's echoes. A track keeps running its effects this long after its last clip.
    /// </summary>
    public virtual double TailSeconds => 0.0;

    /// <summary>
    /// How many samples late its output is: an effect that works on a window of sound (a spectral
    /// one) hands each sample back that much later. Only a clip effect may have latency (see
    /// <see cref="IClipEffect"/>): the mixer reads the clip's file that far ahead, so the clip stays
    /// in time with everything else.
    /// </summary>
    public virtual int LatencySamples => 0;

    /// <summary>The owner sample the next block should start at, where the last one ended; <see cref="long.MinValue"/> after a reset.</summary>
    internal long Next { get; set; } = long.MinValue;

    /// <summary>The rate it was last prepared for.</summary>
    protected int SampleRate { get; private set; }

    /// <summary>The channels it was last prepared for.</summary>
    protected int Channels { get; private set; }

    /// <summary>
    /// The parameters at the end of the last block it ran, so the next one ramps from there even
    /// when the mix was rebuilt in between (a fader moved, a setting typed): a change is a ramp
    /// across one block, never a step. Made by the building thread, once per instance.
    /// </summary>
    internal float[]? Last { get; set; }

    /// <summary>True once <see cref="Last"/> holds the end of a block; false after a reset.</summary>
    internal bool HasLast { get; set; }

    /// <summary>
    /// Gets ready to run at a rate and a channel count. Called on the thread that builds the mix,
    /// never the audio thread, whenever either changes: allocate delay lines and filter state
    /// here. The base remembers them and calls <see cref="OnPrepare"/> when they differ.
    /// </summary>
    public void Prepare(int sampleRate, int channels)
    {
        if (sampleRate == SampleRate && channels == Channels)
        {
            return;
        }

        SampleRate = sampleRate;
        Channels = channels;
        HasLast = false;
        OnPrepare(sampleRate, channels);
    }

    /// <summary>Changes a block in place.</summary>
    public abstract void Process(in AudioEffectBlock block);

    /// <summary>Forgets any state, for a seek: a filter's memory of the last position is wrong at the new one.</summary>
    public virtual void Reset()
    {
    }

    /// <summary>Forgets its state and where its parameters were, for a seek.</summary>
    internal void ResetAll()
    {
        HasLast = false;
        Next = long.MinValue;
        Reset();
    }

    /// <summary>
    /// Takes the value of one of its text parameters (a learned profile, a key track), on the thread
    /// that builds the mix whenever the mix is built. Parse here, never on the audio thread, and hand
    /// the result over by swapping one reference.
    /// </summary>
    public virtual void Text(string name, string value)
    {
    }

    /// <summary>Allocates what it needs for a rate and a channel count.</summary>
    protected virtual void OnPrepare(int sampleRate, int channels)
    {
    }
}

/// <summary>
/// An effect that belongs on a clip, never a track: it reads ahead in the clip's file to make up
/// its latency (noise reduction), which a track, the sum of many clips, cannot.
/// </summary>
public interface IClipEffect;

/// <summary>An effect that belongs on a track, never a clip: it listens to another track (a ducker).</summary>
public interface ITrackEffect;

/// <summary>One effect in a clip's or a track's chain, as the audio thread runs it.</summary>
public sealed class AudioEffectSlot
{
    private readonly ScalarCurve[] _params;
    private readonly float[] _from;
    private readonly float[] _to;

    /// <summary>Creates a slot.</summary>
    /// <param name="id">The effect instance.</param>
    /// <param name="effect">What runs it.</param>
    /// <param name="parameters">Each parameter as a curve over owner time in samples, in declared order.</param>
    public AudioEffectSlot(string id, AudioEffect effect, ScalarCurve[] parameters)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(parameters);

        Id = id;
        Effect = effect;
        _params = parameters;
        _from = new float[parameters.Length];
        _to = new float[parameters.Length];
    }

    /// <summary>The effect instance's identifier.</summary>
    public string Id { get; }

    /// <summary>What runs it.</summary>
    public AudioEffect Effect { get; }

    /// <summary>The track its <c>key</c> parameter names, by id or name, for an effect that listens to another track; null for none.</summary>
    public string? KeyName { get; init; }

    /// <summary>The track it listens to, found by the builder from <see cref="KeyName"/>; null for none.</summary>
    public TrackMix? KeyTrack { get; internal set; }

    /// <summary>Runs the effect over a block. <paramref name="time"/> is the owner's sample at the block's start.</summary>
    public void Process(AudioBuffer buffer, int offset, int frames, int channels, int sampleRate, long time, AudioBuffer? key = null)
    {
        // Where the last block ended, if it ran, rather than where the curves start now: a mix
        // rebuilt with a new setting ramps to it across this block instead of stepping.
        float[]? last = Effect.Last;
        bool continuing = Effect.HasLast && last is not null && last.Length == _params.Length;
        for (int index = 0; index < _params.Length; index++)
        {
            ScalarCurve curve = _params[index];
            _from[index] = continuing ? last![index] : curve.Evaluate(time);
            _to[index] = curve.IsConstant ? curve.Evaluate(time) : curve.Evaluate(time + frames);
        }

        Effect.Process(new AudioEffectBlock(buffer, offset, frames, channels, sampleRate, _from, _to, key, time));
        Effect.Next = time + frames;

        if (last is not null && last.Length == _params.Length)
        {
            _to.CopyTo(last, 0);
            Effect.HasLast = true;
        }
    }
}

/// <summary>
/// Keeps effect instances alive across mix rebuilds, and builds the chains.
/// </summary>
/// <remarks>
/// One per graph. The builder asks it for each effect by identifier; an effect still there after
/// an edit gets the instance it had, with its state, and one that has gone is dropped at the end
/// of the build. Used from the building thread only; the audio thread only ever sees the slots.
/// </remarks>
public sealed class AudioEffectHost
{
    private readonly object _gate = new();
    private Dictionary<string, (string TypeId, AudioEffect Effect)> _live = new(StringComparer.Ordinal);

    /// <summary>Creates a host over a registry.</summary>
    public AudioEffectHost(EffectRegistry? registry = null) => Registry = registry ?? AudioEffects.Registry;

    /// <summary>The effect types it can make.</summary>
    public EffectRegistry Registry { get; }

    /// <summary>True for a graph that renders an export: plugins are told they render offline (Phase 46).</summary>
    public bool Offline { get; init; }

    /// <summary>
    /// The chain for a list of effects: enabled sound effects this build has, in order. Keyframe
    /// times are converted to samples once, here.
    /// </summary>
    /// <param name="effects">The effects on a clip or a track.</param>
    /// <param name="sampleRate">The mix rate.</param>
    /// <param name="channels">The channels the chain runs on: a clip's source's, or the mix's for a track.</param>
    /// <param name="keep">Collects the ids used, for <see cref="Retain"/>.</param>
    public AudioEffectSlot[] Chain(EquatableArray<Effect> effects, int sampleRate, int channels, ISet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);

        if (effects.IsEmpty)
        {
            return [];
        }

        var slots = new List<AudioEffectSlot>();
        lock (_gate)
        {
            foreach (Effect effect in effects)
            {
                if (!effect.Enabled || Registry.Find(effect.TypeId) is not { Kind: EffectKind.Audio, Implementation: { } type } descriptor
                    || !typeof(AudioEffect).IsAssignableFrom(type))
                {
                    continue;
                }

                if (!_live.TryGetValue(effect.Id, out var live) || !string.Equals(live.TypeId, effect.TypeId, StringComparison.Ordinal))
                {
                    live = (effect.TypeId, (AudioEffect)Activator.CreateInstance(type)!);
                    _live[effect.Id] = live;
                }

                live.Effect.Prepare(sampleRate, channels);
                foreach (ParamDescriptor text in descriptor.Params.Where(parameter => parameter.Type == ParamType.Text))
                {
                    live.Effect.Text(text.Name, effect.Parameter(text.Name) is StaticValue { Value: ParamValue.Text value } ? value.Value : string.Empty);
                }

                if (live.Effect is PluginEffect plugin)
                {
                    plugin.EffectId = effect.Id;
                    plugin.Offline = Offline;
                    plugin.Automate(PluginCurves(effect, sampleRate));
                }

                live.Effect.Last ??= new float[descriptor.Params.Length];
                keep.Add(effect.Id);
                slots.Add(new AudioEffectSlot(effect.Id, live.Effect, Curves(descriptor, effect, sampleRate))
                {
                    KeyName = descriptor.Param("key") is { Type: ParamType.Text } && effect.Parameter("key") is StaticValue { Value: ParamValue.Text { Value.Length: > 0 } key }
                        ? key.Value
                        : null,
                });
            }
        }

        return [.. slots];
    }

    /// <summary>Drops the instances of effects that were not used by the last build.</summary>
    public void Retain(ISet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);

        lock (_gate)
        {
            if (_live.Keys.All(used.Contains))
            {
                return;
            }

            foreach ((string id, (string TypeId, AudioEffect Effect) live) in _live.Where(entry => !used.Contains(entry.Key)))
            {
                (live.Effect as PluginEffect)?.Stop();
            }

            _live = _live.Where(entry => used.Contains(entry.Key)).ToDictionary(StringComparer.Ordinal);
        }
    }

    /// <summary>A plugin effect's own parameters (<c>p&lt;id&gt;</c>) as curves, by their CLAP ids.</summary>
    private static List<(uint Id, ScalarCurve Curve)> PluginCurves(Effect effect, int sampleRate)
    {
        var curves = new List<(uint Id, ScalarCurve Curve)>();
        foreach (EffectParameter parameter in effect.Parameters)
        {
            if (!parameter.Name.StartsWith(PluginEffect.ParameterPrefix, StringComparison.Ordinal)
                || !uint.TryParse(parameter.Name.AsSpan(PluginEffect.ParameterPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint id))
            {
                continue;
            }

            ScalarCurve? curve = parameter.Value switch
            {
                StaticValue { Value: ParamValue.Float value } => ScalarCurve.Constant(value.Value),
                KeyframedValue { Keyframes.IsEmpty: false } keyed => ScalarCurve.From(keyed, 0f, sampleRate),
                _ => null,
            };

            if (curve is not null)
            {
                curves.Add((id, curve));
            }
        }

        return curves;
    }

    /// <summary>A parameter as the number the audio thread reads, at its declared type and inside its limits.</summary>
    public static float Numeric(ParamDescriptor descriptor, ParamValue value)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(value);

        ParamValue typed = ParamValues.Coerce(descriptor, value) is { } coerced ? ParamValues.Clamp(descriptor, coerced) : descriptor.Default;
        return typed switch
        {
            ParamValue.Float number => number.Value,
            ParamValue.Int whole => whole.Value,
            ParamValue.Bool flag => flag.Value ? 1.0f : 0.0f,
            ParamValue.Enum member => Math.Max(0, descriptor.Choices.IndexOf(choice => string.Equals(choice, member.Value, StringComparison.Ordinal))),
            _ => 0.0f,
        };
    }

    private static ScalarCurve[] Curves(EffectDescriptor descriptor, Effect effect, int sampleRate)
    {
        ImmutableArray<ParamDescriptor> parameters = descriptor.Params;
        var curves = new ScalarCurve[parameters.Length];

        for (int index = 0; index < parameters.Length; index++)
        {
            ParamDescriptor parameter = parameters[index];
            float fallback = Numeric(parameter, parameter.Default);

            curves[index] = effect.Parameter(parameter.Name) switch
            {
                StaticValue constant => ScalarCurve.Constant(Numeric(parameter, constant.Value)),
                KeyframedValue { Keyframes.IsEmpty: false } keyed => ScalarCurve.From(
                    new KeyframedValue(keyed.Keyframes.Select(key => key with
                    {
                        Value = new ParamValue.Float(Numeric(parameter, key.Value)),
                        Interp = parameter.Type == ParamType.Float ? key.Interp : Interp.Hold,
                    })),
                    fallback,
                    sampleRate),
                _ => ScalarCurve.Constant(fallback),
            };
        }

        return curves;
    }
}

/// <summary>The sound effects this assembly defines.</summary>
public static class AudioEffects
{
    /// <summary>Every effect class in JazzHands.Audio.</summary>
    public static EffectRegistry Registry { get; } = EffectRegistry.FromAssemblies(typeof(AudioEffects).Assembly);
}
