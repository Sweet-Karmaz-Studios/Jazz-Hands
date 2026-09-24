namespace JazzHands.Core.Commands;

/// <summary>Every title preset: the built-in ones and a person's own, with what each sets.</summary>
[Query("title.list-presets", Description = "List the title presets and what each sets")]
public sealed record ListTitlePresetsQuery : IQuery<TitlePresetInfo[]>;
