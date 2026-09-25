using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Uses another track as the matte of every clip on a track that has none of its own.</summary>
/// <param name="TrackId">Which track.</param>
/// <param name="Source">The video track whose picture is the matte, in the same sequence.</param>
/// <param name="Mode">alpha, luma, alpha-inverted or luma-inverted.</param>
/// <param name="Off">Take the track's matte away.</param>
[Command("track.set-matte", Description = "Show a track only through another track's picture (a track matte)")]
public sealed record SetTrackMatteCommand(
    [property: Arg(0, "The track id")] string TrackId,
    [property: Option("source", "The track whose picture is the matte")] string? Source = null,
    [property: Option("mode", "alpha, luma, alpha-inverted or luma-inverted. Default: alpha")] TrackMatteMode Mode = TrackMatteMode.Alpha,
    [property: Option("off", "Take the track's matte away")] bool Off = false) : ICommand;
