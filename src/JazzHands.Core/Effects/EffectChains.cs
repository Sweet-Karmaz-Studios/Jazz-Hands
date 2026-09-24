using System.Text.Json;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;

namespace JazzHands.Core.Effects;

/// <summary>
/// A clip's or a track's effects as a person sees them: in order, without a generator's own
/// parameters.
/// </summary>
/// <remarks>
/// A generator clip stores its parameters as an effect of its own type (a solid's colour is a
/// <c>gen.solid</c> effect on the clip), so its chain holds one entry that is not an effect in
/// the chain at all. Indexes the commands and the inspector use count the others, so "index 0"
/// is the first effect whatever the clip is.
/// </remarks>
public static class EffectChains
{
    /// <summary>True when an effect is a generator clip's own parameters rather than an effect on it.</summary>
    public static bool IsOwnParameters(Clip? clip, Effect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return clip?.GeneratorId is { } generator && string.Equals(effect.TypeId, generator, StringComparison.Ordinal);
    }

    /// <summary>The effects in a chain, in order, without a generator's own parameters.</summary>
    public static IReadOnlyList<Effect> Visible(Clip? clip, EquatableArray<Effect> effects) =>
        [.. effects.Where(effect => !IsOwnParameters(clip, effect))];

    /// <summary>Where an effect sits among the visible ones, or -1.</summary>
    public static int IndexOf(Clip? clip, EquatableArray<Effect> effects, string effectId)
    {
        IReadOnlyList<Effect> visible = Visible(clip, effects);
        for (int index = 0; index < visible.Count; index++)
        {
            if (string.Equals(visible[index].Id, effectId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// A chain with effects inserted before the visible effect at <paramref name="index"/>, or at
    /// the end when the index is null or past the last.
    /// </summary>
    public static EquatableArray<Effect> Insert(Clip? clip, EquatableArray<Effect> effects, int? index, IEnumerable<Effect> added)
    {
        ArgumentNullException.ThrowIfNull(added);

        IReadOnlyList<Effect> visible = Visible(clip, effects);
        int stored = index is { } at && at < visible.Count
            ? effects.IndexOf(effect => ReferenceEquals(effect, visible[at]) || effect.Id == visible[at].Id)
            : effects.Length;

        var list = new List<Effect>(effects);
        list.InsertRange(stored, added);
        return new EquatableArray<Effect>(list);
    }

    /// <summary>A chain with one effect moved to another visible index.</summary>
    public static EquatableArray<Effect> Move(Clip? clip, EquatableArray<Effect> effects, string effectId, int index)
    {
        Effect moving = effects.First(effect => string.Equals(effect.Id, effectId, StringComparison.Ordinal));
        EquatableArray<Effect> without = new(effects.Where(effect => !ReferenceEquals(effect, moving)));
        return Insert(clip, without, index, [moving]);
    }

    /// <summary>Copies of effects with fresh identifiers, for a paste or a preset applied.</summary>
    public static IEnumerable<Effect> WithNewIds(IEnumerable<Effect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        return effects.Select(effect => effect with { Id = Id.New() });
    }
}

/// <summary>
/// What <c>effect.copy</c> puts on the clipboard and <c>effect.paste</c> reads: the effects
/// whole, keyframes and all, in the project file's own JSON.
/// </summary>
/// <param name="Effects">The effects, in order.</param>
public sealed record EffectClipboard(EquatableArray<Effect> Effects)
{
    /// <summary>The clipboard format the editor puts this under.</summary>
    public const string Format = "JazzHands.Effects";

    /// <summary>As JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JazzJson.Options);

    /// <summary>Reads what <see cref="ToJson"/> wrote, or null when the text is not that.</summary>
    public static EffectClipboard? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EffectClipboard>(json, JazzJson.Options) is { Effects.IsEmpty: false } content
                && content.Effects.All(effect => !string.IsNullOrWhiteSpace(effect.TypeId))
                ? content
                : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A preset as JSON, for <c>effect.export-preset</c>.</summary>
    public static string PresetToJson(EffectPreset preset) => JsonSerializer.Serialize(preset, JazzJson.Options);

    /// <summary>Reads a preset, or null when the text is not one.</summary>
    public static EffectPreset? PresetFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EffectPreset>(json, JazzJson.Options) is { Name.Length: > 0, Effects.IsEmpty: false } preset
                && preset.Effects.All(effect => !string.IsNullOrWhiteSpace(effect.TypeId))
                ? preset
                : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
