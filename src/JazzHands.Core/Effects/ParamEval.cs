using System.Collections.Immutable;
using System.Numerics;
using JazzHands.Core.Animation;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Effects;

/// <summary>
/// What a described parameter is worth at a time.
/// </summary>
/// <remarks>
/// The one place every renderer and mixer asks. Keyframes are evaluated by
/// <see cref="AnimationEvaluator"/>, a value of the wrong type (a hand edit) becomes the declared
/// type or the default, and a value outside the declared limits is held inside them. Phase 29a's
/// drivers replace a keyframe curve with an expression, and this is where that happens: a new
/// <see cref="AnimatedValue"/> case evaluated here, and nothing that asks for a value changes.
/// </remarks>
public static class ParamEval
{
    /// <summary>The value of a parameter at a time, relative to whatever owns it.</summary>
    /// <param name="value">What the project holds, or null for the default.</param>
    /// <param name="descriptor">What the parameter is.</param>
    /// <param name="time">When, relative to the owner: clip time for a clip's parameters, sequence time for a track's.</param>
    /// <param name="evaluator">An evaluator that remembers the last segment, for a caller asking every frame.</param>
    public static ParamValue Eval(AnimatedValue? value, ParamDescriptor descriptor, Flicks time, AnimationEvaluator? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (value is null or KeyframedValue { Keyframes.IsEmpty: true })
        {
            return descriptor.Default;
        }

        ParamValue raw = evaluator is null ? AnimationEvaluator.Evaluate(value, time) : evaluator.Eval(value, time);
        return ParamValues.Coerce(descriptor, raw) is { } typed ? ParamValues.Clamp(descriptor, typed) : descriptor.Default;
    }
}

/// <summary>
/// An effect's parameters evaluated at one moment, in its declared order.
/// </summary>
/// <remarks>
/// What a render pass or a mix block reads. Every value is already the declared type and inside
/// its limits, so an effect reads <c>Float("radius")</c> without checking anything.
/// </remarks>
public sealed class ParameterSet
{
    private ParameterSet(EffectDescriptor descriptor, ImmutableArray<ParamValue> values)
    {
        Descriptor = descriptor;
        Values = values;
    }

    /// <summary>What the values are of.</summary>
    public EffectDescriptor Descriptor { get; }

    /// <summary>The values, in <see cref="EffectDescriptor.Params"/> order.</summary>
    public ImmutableArray<ParamValue> Values { get; }

    /// <summary>Every parameter at its default.</summary>
    public static ParameterSet Defaults(EffectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new ParameterSet(descriptor, [.. descriptor.Params.Select(parameter => parameter.Default)]);
    }

    /// <summary>An effect instance's parameters at a time relative to its owner.</summary>
    public static ParameterSet Evaluate(EffectDescriptor descriptor, Effect? effect, Flicks time)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var values = ImmutableArray.CreateBuilder<ParamValue>(descriptor.Params.Length);
        foreach (ParamDescriptor parameter in descriptor.Params)
        {
            values.Add(ParamEval.Eval(effect?.Parameter(parameter.Name), parameter, time));
        }

        return new ParameterSet(descriptor, values.MoveToImmutable());
    }

    /// <summary>A copy with one value replaced, for tests and previews.</summary>
    public ParameterSet With(string name, ParamValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int index = Descriptor.IndexOf(name);
        if (index < 0)
        {
            throw new ArgumentException($"'{Descriptor.TypeId}' has no parameter '{name}'.", nameof(name));
        }

        ParamValue typed = ParamValues.Coerce(Descriptor.Params[index], value)
            ?? throw new ArgumentException($"'{name}' is a {Descriptor.Params[index].Type}, not a {value.TypeName}.", nameof(value));
        return new ParameterSet(Descriptor, Values.SetItem(index, typed));
    }

    /// <summary>A value by name.</summary>
    public ParamValue this[string name]
    {
        get
        {
            int index = Descriptor.IndexOf(name);
            return index >= 0 ? Values[index] : throw new ArgumentException($"'{Descriptor.TypeId}' has no parameter '{name}'.", nameof(name));
        }
    }

    /// <summary>A number.</summary>
    public float Float(string name) => this[name] is ParamValue.Float value ? value.Value : 0.0f;

    /// <summary>A pair, or a point.</summary>
    public Vector2 Float2(string name) => this[name] is ParamValue.Float2 value ? value.Value : Vector2.Zero;

    /// <summary>Four numbers.</summary>
    public Vector4 Float4(string name) => this[name] is ParamValue.Float4 value ? value.Value : Vector4.Zero;

    /// <summary>A colour, linear and premultiplied.</summary>
    public Vector4 Color(string name) => this[name] is ParamValue.Color value ? value.Value : Vector4.Zero;

    /// <summary>A switch.</summary>
    public bool Bool(string name) => this[name] is ParamValue.Bool { Value: true };

    /// <summary>A whole number.</summary>
    public int Int(string name) => this[name] is ParamValue.Int value ? value.Value : 0;

    /// <summary>The chosen name of an enum.</summary>
    public string Enum(string name) => this[name] is ParamValue.Enum value ? value.Value : string.Empty;

    /// <summary>Text or a path.</summary>
    public string Text(string name) => this[name] switch
    {
        ParamValue.Text text => text.Value,
        ParamValue.Path path => path.Value,
        _ => string.Empty,
    };
}
