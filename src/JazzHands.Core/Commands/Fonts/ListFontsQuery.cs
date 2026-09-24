namespace JazzHands.Core.Commands;

/// <summary>The font families a title can use: those installed, and those in the project's fonts folder.</summary>
/// <remarks>
/// A project's own fonts are the files in a <c>fonts</c> folder beside the project file. They are
/// used before installed ones of the same name, so a project carries its look to another machine.
/// </remarks>
/// <param name="Search">Only families whose name contains this.</param>
[Query("fonts.list", Description = "List the font families titles can use")]
public sealed record ListFontsQuery(
    [property: Option("search", "Only families whose name contains this")] string? Search = null) : IQuery<FontInfo[]>;
