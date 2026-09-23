namespace JazzHands.Core.Commands;

/// <summary>Asks for the files the project uses.</summary>
/// <param name="Folder">Only this bin folder and what is under it.</param>
/// <param name="Search">Only items whose name, tags or file name contain this.</param>
/// <param name="Tag">Only items carrying this tag.</param>
[Query("media.list", Description = "List the files the project uses")]
public sealed record ListMediaQuery(
    [property: Option("folder", "Only this bin folder")] string? Folder = null,
    [property: Option("search", "Only items matching this text")] string? Search = null,
    [property: Option("tag", "Only items carrying this tag")] string? Tag = null) : IQuery<MediaItemInfo[]>;
