using JazzHands.Core.Diagnostics;

namespace JazzHands.Core.Commands;

/// <summary>
/// Asks what the session has noticed that a person should know about.
/// </summary>
/// <remarks>
/// Fallbacks, missing files and formats that will not play as well as they should. The media
/// panel badges clips with this and <c>jazz diagnostics list</c> prints it, from one list, so
/// neither can be right while the other is wrong.
/// </remarks>
/// <param name="MediaId">Only this media item, or everything when left out.</param>
/// <param name="Level">Only diagnostics at least this serious.</param>
[Query("diagnostics.list", Description = "List what the session has noticed: fallbacks, missing files")]
public sealed record ListDiagnosticsQuery(
    [property: Option("media", "Only this media item")] string? MediaId = null,
    [property: Option("level", "information, warning or error")] DiagnosticLevel Level = DiagnosticLevel.Information)
    : IQuery<Diagnostic[]>;
