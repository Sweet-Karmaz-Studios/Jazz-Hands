namespace JazzHands.Core.Commands;

/// <summary>Sets motion blur for every animated layer in a sequence, unless a track or clip says otherwise.</summary>
/// <param name="Angle">How long the shutter is open, in degrees of a frame: 180 is half a frame.</param>
/// <param name="Samples">How many moments across the shutter are averaged, 2 to 64.</param>
/// <param name="Off">Turn it off.</param>
/// <param name="SequenceId">Which sequence. The active one when left out.</param>
[Command("sequence.set-motion-blur", Description = "Set motion blur for every animated layer in a sequence")]
public sealed record SetSequenceMotionBlurCommand(
    [property: Option("angle", "Shutter angle in degrees: 180 is half a frame. Default: 180")] double? Angle = null,
    [property: Option("samples", "Moments averaged, 2 to 64. Default: 32")] int? Samples = null,
    [property: Option("off", "Turn it off")] bool Off = false,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
