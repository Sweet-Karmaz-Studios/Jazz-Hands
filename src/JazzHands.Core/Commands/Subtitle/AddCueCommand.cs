using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Adds a subtitle cue to a subtitle track.</summary>
/// <remarks>Cues may overlap; overlapping cues stack. The text is title markup: <c>[i]</c>, <c>[b]</c>, <c>\n</c> for a new line.</remarks>
/// <param name="TrackId">The subtitle track.</param>
/// <param name="At">When it appears.</param>
/// <param name="Duration">How long it stays.</param>
/// <param name="Text">What it says.</param>
/// <param name="Align">Where it sits; bottom when not given.</param>
/// <param name="CueId">The identifier to give it.</param>
[Command("subtitle.add", Description = "Add a subtitle cue")]
public sealed record AddCueCommand(
    [property: Arg(0, "The subtitle track id")] string TrackId,
    [property: Option("at", "When it appears")] Flicks At,
    [property: Option("dur", "How long it stays")] Flicks Duration,
    [property: Option("text", "What it says, as markup")] string Text,
    [property: Option("align", "bottom, top, middle, bottom-left and so on")] SubtitleAlign Align = SubtitleAlign.Bottom,
    [property: Option("id", "The identifier to give it")] string? CueId = null) : ICommand;
