namespace JazzHands.Core.Commands;

/// <summary>Keeps a multicam's sound on one angle whatever the picture, or lets it follow the switches.</summary>
/// <param name="ClipId">The multicam clip.</param>
/// <param name="Angle">The angle to hear throughout, from 1; 0 for sound that follows the switches.</param>
[Command("multicam.set-sound", Description = "Keep a multicam's sound on one angle, or let it follow")]
public sealed record SetMulticamSoundCommand(
    [property: Arg(0, "The multicam clip id")] string ClipId,
    [property: Option("angle", "The angle to hear throughout, from 1; 0 to follow the switches")] int Angle) : ICommand;
