using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Moves a subtitle cue to another place on the frame.</summary>
/// <remarks>Top is where a cue goes when something at the bottom of the picture must not be covered.</remarks>
/// <param name="CueId">The cue.</param>
/// <param name="Align">Where it sits.</param>
[Command("subtitle.set-align", Description = "Move a subtitle cue to another place on the frame")]
public sealed record SetCueAlignCommand(
    [property: Arg(0, "The cue id")] string CueId,
    [property: Arg(1, "bottom, top, middle, bottom-left and so on")] SubtitleAlign Align) : ICommand;
