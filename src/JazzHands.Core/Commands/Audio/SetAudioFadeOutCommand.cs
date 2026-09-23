using JazzHands.Core.Model;
using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Sets how an audio clip fades out.</summary>
/// <param name="ClipId">Which clip. It must be on an audio track.</param>
/// <param name="Duration">How long the fade lasts. Zero removes it.</param>
/// <param name="Curve">
/// The shape: linear; ease-in-out for the smooth raised cosine; ease-in to start slowly; ease-out to
/// start quickly; bezier for a steeper S curve.
/// </param>
[Command("audio.set-fade-out", Description = "Set an audio clip's fade out")]
public sealed record SetAudioFadeOutCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("dur", "How long the fade lasts; 0 removes it")] Flicks Duration,
    [property: Option("curve", "linear, ease-in-out (smooth), ease-in (slow start), ease-out (fast start) or bezier (S curve)")] Interp Curve = Interp.Linear) : ICommand;
