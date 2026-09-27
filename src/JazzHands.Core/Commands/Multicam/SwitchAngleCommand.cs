using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Cuts a multicam clip to another angle from a time on.</summary>
/// <remarks>
/// The switch is kept in the multicam's sequence, on its frame, so every clip of the same
/// multicam shows it. Angles are numbered from 1, as the 1 to 9 keys are.
/// </remarks>
/// <param name="ClipId">The multicam clip.</param>
/// <param name="At">When, on the timeline.</param>
/// <param name="Angle">The angle, from 1.</param>
/// <param name="PictureOnly">Switch the picture and keep the sound.</param>
/// <param name="SoundOnly">Switch the sound and keep the picture.</param>
[Command("multicam.switch", Description = "Cut a multicam clip to another angle")]
public sealed record SwitchAngleCommand(
    [property: Arg(0, "The multicam clip id")] string ClipId,
    [property: Option("at", "When, on the timeline")] Flicks At,
    [property: Option("angle", "The angle, from 1")] int Angle,
    [property: Option("video-only", "Switch the picture and keep the sound")] bool PictureOnly = false,
    [property: Option("audio-only", "Switch the sound and keep the picture")] bool SoundOnly = false) : ICommand;
