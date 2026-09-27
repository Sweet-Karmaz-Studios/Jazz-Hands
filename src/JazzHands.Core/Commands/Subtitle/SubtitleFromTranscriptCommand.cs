using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Makes captions on a subtitle track from what is said.</summary>
/// <remarks>
/// Lays the words of a clip, or of the whole sequence, out as cues: a cue ends at a sentence's
/// end, at a pause, or when the next word would not fit its lines; lines are balanced; each cue
/// stays up long enough to read, and leaves the minimum gap before the next. The line length and
/// count come from the track's style (42 and 2). Cues already on the track over the same stretch
/// are replaced. <c>subtitle.check</c> checks the result; the SRT and WebVTT export takes it, or
/// it burns in. One undo.
/// </remarks>
/// <param name="TrackId">The subtitle track.</param>
/// <param name="ClipId">Only the words of this clip; the whole sequence when left out.</param>
/// <param name="MaxChars">The most characters a line.</param>
/// <param name="MaxLines">The most lines a cue.</param>
/// <param name="MinGapFrames">The fewest frames between cues that do not run straight on.</param>
[Command("subtitle.from-transcript", Description = "Make captions from the transcript")]
public sealed record SubtitleFromTranscriptCommand(
    [property: Arg(0, "The subtitle track id")] string TrackId,
    [property: Option("clip", "Only this clip's words")] string? ClipId = null,
    [property: Option("max-chars", "The most characters a line")] int? MaxChars = null,
    [property: Option("max-lines", "The most lines a cue")] int? MaxLines = null,
    [property: Option("min-gap", "The fewest frames between cues. Default: 2")] int MinGapFrames = 2) : ICommand;
