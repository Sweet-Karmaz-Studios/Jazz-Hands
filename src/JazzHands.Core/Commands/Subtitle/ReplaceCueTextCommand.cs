namespace JazzHands.Core.Commands;

/// <summary>Finds text in a subtitle track's cues and replaces it.</summary>
/// <remarks>Matches the text as it reads, not its markup, so a word half in italics is found; formatting stays where it was around what changed. One undo step.</remarks>
/// <param name="TrackId">The subtitle track.</param>
/// <param name="Find">What to look for.</param>
/// <param name="With">What to put there; nothing removes it.</param>
/// <param name="MatchCase">True to match capitals exactly.</param>
[Command("subtitle.replace", Description = "Find and replace text in a subtitle track")]
public sealed record ReplaceCueTextCommand(
    [property: Arg(0, "The subtitle track id")] string TrackId,
    [property: Option("find", "What to look for")] string Find,
    [property: Option("with", "What to put there")] string With = "",
    [property: Option("match-case", "Match capitals exactly")] bool MatchCase = false) : ICommand;
