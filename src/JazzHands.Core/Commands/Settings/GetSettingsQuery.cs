namespace JazzHands.Core.Commands;

/// <summary>Lists the editor's settings: each key, its value, and its default.</summary>
/// <param name="Section">Only this section (<c>editor</c>, <c>cache</c>, <c>control</c>, <c>recent</c>).</param>
[Query("settings.get", Description = "List the editor's settings with their values and defaults")]
public sealed record GetSettingsQuery(
    [property: Option("section", "Only this section")] string? Section = null) : IQuery<SettingInfo[]>;

/// <summary>One setting.</summary>
/// <param name="Key">Section and name, as <c>settings.set</c> takes it.</param>
/// <param name="Value">Its value, as JSON.</param>
/// <param name="Default">What it is when nobody has set it, as JSON.</param>
/// <param name="Type">What it takes: text, true or false, a whole number, a number, a list or an object.</param>
/// <param name="Description">What the section is for.</param>
public sealed record SettingInfo(string Key, string Value, string Default, string Type, string Description);
