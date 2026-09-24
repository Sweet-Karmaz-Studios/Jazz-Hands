using System.Windows;

namespace JazzHands.App.Services;

/// <summary>
/// What an effect or a preset carries while it is dragged from the effects panel: its type id or
/// preset id, under a format of its own so nothing else mistakes it for text.
/// </summary>
public static class EffectDragData
{
    /// <summary>The clipboard format for an effect type.</summary>
    public const string EffectFormat = "JazzHands.EffectType.v1";

    /// <summary>The clipboard format for an effect preset.</summary>
    public const string PresetFormat = "JazzHands.EffectPreset.v1";

    /// <summary>A drag of one effect type.</summary>
    public static DataObject ForEffect(string typeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        var data = new DataObject();
        data.SetData(EffectFormat, typeId);
        return data;
    }

    /// <summary>A drag of one preset.</summary>
    public static DataObject ForPreset(string presetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        var data = new DataObject();
        data.SetData(PresetFormat, presetId);
        return data;
    }

    /// <summary>The effect type a drag carries, or null.</summary>
    public static string? Effect(IDataObject? data) =>
        data?.GetDataPresent(EffectFormat) == true && data.GetData(EffectFormat) is string { Length: > 0 } id ? id : null;

    /// <summary>The preset a drag carries, or null.</summary>
    public static string? Preset(IDataObject? data) =>
        data?.GetDataPresent(PresetFormat) == true && data.GetData(PresetFormat) is string { Length: > 0 } id ? id : null;
}
