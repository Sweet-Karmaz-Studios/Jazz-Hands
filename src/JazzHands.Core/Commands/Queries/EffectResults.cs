using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>What a parameter takes: its type, default and limits. Values are text as the commands read them.</summary>
/// <param name="Name">The name the commands use.</param>
/// <param name="Type">float, float2, point, float4, color, bool, int, enum, text or path.</param>
/// <param name="Label">What the inspector calls it.</param>
/// <param name="Description">One sentence on what it does.</param>
/// <param name="Default">Its value when nothing is set.</param>
/// <param name="Min">The smallest value accepted, for numbers.</param>
/// <param name="Max">The largest value accepted, for numbers.</param>
/// <param name="Unit">The unit: px, deg, %, dB, or nothing.</param>
/// <param name="Animatable">False for a parameter that cannot be keyframed.</param>
/// <param name="Choices">For an enum, the names it takes.</param>
public sealed record ParamSchemaInfo(
    string Name,
    string Type,
    string Label,
    string Description,
    string Default,
    double? Min,
    double? Max,
    string Unit,
    bool Animatable,
    string[] Choices)
{
    /// <summary>The schema of a descriptor.</summary>
    public static ParamSchemaInfo From(ParamDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new ParamSchemaInfo(
            descriptor.Name,
            CommandValues.ToKebabCase(descriptor.Type.ToString()),
            descriptor.DisplayName,
            descriptor.Description,
            ParamValues.Format(descriptor.Default),
            descriptor.Min,
            descriptor.Max,
            descriptor.Unit,
            descriptor.Animatable,
            [.. descriptor.Choices]);
    }
}

/// <summary>One effect type.</summary>
/// <param name="TypeId">The identifier effect.add takes.</param>
/// <param name="Kind">video, audio or generator.</param>
/// <param name="Name">What the browser calls it.</param>
/// <param name="Category">The browser folder.</param>
/// <param name="Description">One sentence on what it does.</param>
/// <param name="Params">Its parameters, in order.</param>
public sealed record EffectTypeInfo(
    string TypeId,
    EffectKind Kind,
    string Name,
    string Category,
    string Description,
    ParamSchemaInfo[] Params);

/// <summary>One keyframe.</summary>
/// <param name="Time">Where it is on the sequence.</param>
/// <param name="Local">Where it is from its owner's start, which is what the file stores.</param>
/// <param name="Value">Its value, as text.</param>
/// <param name="Interp">How the curve leaves it.</param>
/// <param name="In">The handle arriving, "time, value", when set.</param>
/// <param name="Out">The handle leaving, when set.</param>
public sealed record KeyframeInfo(
    Flicks Time,
    Flicks Local,
    string Value,
    Interp Interp,
    string? In,
    string? Out);

/// <summary>One parameter of a clip, track, effect or mask, as it stands.</summary>
/// <param name="OwnerId">What it belongs to.</param>
/// <param name="Name">The name the commands use.</param>
/// <param name="Section">The inspector section: Transform, Opacity, Crop, Audio, Mask, or the effect's name.</param>
/// <param name="Schema">What it takes.</param>
/// <param name="IsDefault">True when nothing is stored and the default applies.</param>
/// <param name="IsAnimated">True when it has keyframes.</param>
/// <param name="Value">Its value: the constant, or what it is worth at the time asked (the owner's start otherwise).</param>
/// <param name="Keyframes">Its keyframes, in time order.</param>
public sealed record ParamInfo(
    string OwnerId,
    string Name,
    string Section,
    ParamSchemaInfo Schema,
    bool IsDefault,
    bool IsAnimated,
    string Value,
    KeyframeInfo[] Keyframes);

/// <summary>One effect on a clip or a track.</summary>
/// <param name="Id">The effect identifier.</param>
/// <param name="TypeId">Its type.</param>
/// <param name="Name">The type's name, or the id when this build does not know the type.</param>
/// <param name="Known">False for a type this build has no effect for; it is kept but does nothing.</param>
/// <param name="Enabled">False when bypassed.</param>
/// <param name="OwnerId">The clip or track it is on.</param>
/// <param name="Index">Its place in the chain, from 0.</param>
/// <param name="Params">Its parameters.</param>
public sealed record EffectInfo(
    string Id,
    string TypeId,
    string Name,
    bool Known,
    bool Enabled,
    string OwnerId,
    int Index,
    ParamInfo[] Params);

/// <summary>One effect preset.</summary>
/// <param name="Id">The preset identifier.</param>
/// <param name="Name">Its name.</param>
/// <param name="TypeIds">The effect types in it, in order.</param>
public sealed record EffectPresetInfo(
    string Id,
    string Name,
    string[] TypeIds);
