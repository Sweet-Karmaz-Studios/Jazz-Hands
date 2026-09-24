using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Pastes clips that <c>clipboard.copy</c> copied, in this project or another.</summary>
/// <remarks>
/// Each clip goes on the track with the same number as the one it came from (V2 to V2), or with
/// --track, the lowest copied track of that kind lands there and the rest keep their spacing.
/// Media the project already has, by content hash or path, is reused; the rest is added. Every
/// clip gets a new id, and clips that were linked or grouped are linked and grouped again.
/// </remarks>
/// <param name="Data">What <c>clipboard.copy</c> returned.</param>
/// <param name="At">Where the earliest clip goes.</param>
/// <param name="TrackId">The track for the lowest copied track of its kind.</param>
/// <param name="Insert">Push what is there on instead of pasting over it.</param>
/// <param name="SequenceId">Which sequence. The active one when left out.</param>
[Command("clip.paste", Description = "Paste copied clips at a time, over what is there or pushing it on")]
public sealed record PasteClipsCommand(
    [property: Option("data", "What clipboard.copy returned")] string Data,
    [property: Option("at", "Where the earliest clip goes")] Flicks At,
    [property: Option("track", "The track for the lowest copied track of its kind")] string? TrackId = null,
    [property: Option("insert", "Push what is there on instead of pasting over it")] bool Insert = false,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
