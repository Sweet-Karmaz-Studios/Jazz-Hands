using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Copies clips, with the media they play, as the JSON <c>clip.paste</c> takes.</summary>
/// <remarks>
/// A query because copying changes nothing. The editor puts the answer on the Windows clipboard;
/// a script keeps it and hands it to <c>clip.paste --data</c>, in this project or another.
/// </remarks>
/// <param name="ClipIds">The clips, all in one sequence.</param>
[Query("clipboard.copy", Description = "Copy clips as JSON for clip.paste")]
public sealed record CopyClipsQuery(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds) : IQuery<string>;
