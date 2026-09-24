namespace JazzHands.Core.Commands;

/// <summary>Turns magnetic mode on or off for a sequence.</summary>
/// <remarks>
/// With it on, the primary picture track (the lowest) never has a gap: after every edit the
/// gaps on it close, with the sync-locked tracks, and a clip moved along it goes in at the
/// nearest cut and pushes the rest on rather than landing on top of anything.
/// </remarks>
/// <param name="Magnetic">True to turn it on.</param>
/// <param name="SequenceId">Which sequence. The active one when left out.</param>
[Command("timeline.set-magnetic", Description = "Keep the primary picture track gapless")]
public sealed record SetTimelineMagneticCommand(
    [property: Arg(0, "true to turn it on")] bool Magnetic,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null) : ICommand;
