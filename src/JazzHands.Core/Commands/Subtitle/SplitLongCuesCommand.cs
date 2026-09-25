namespace JazzHands.Core.Commands;

/// <summary>Breaks cues that are too long to read into lines, and into more cues.</summary>
/// <remarks>
/// A cue is laid out in lines of at most the character count, breaking between words; when that
/// takes more than the line count, it becomes several cues, broken at the end of a sentence where
/// one falls near the middle, and its time is shared among them by how much each says. Formatting
/// is kept on the text it covered. Defaults come from the track's style (42 characters, 2 lines).
/// </remarks>
/// <param name="TrackId">The subtitle track.</param>
/// <param name="MaxChars">The most characters a line should have.</param>
/// <param name="MaxLines">The most lines a cue should have.</param>
[Command("subtitle.split-long", Description = "Break long subtitle cues into lines and shorter cues")]
public sealed record SplitLongCuesCommand(
    [property: Arg(0, "The subtitle track id")] string TrackId,
    [property: Option("max-chars", "The most characters a line")] int? MaxChars = null,
    [property: Option("max-lines", "The most lines a cue")] int? MaxLines = null) : ICommand;
