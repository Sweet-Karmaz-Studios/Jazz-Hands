using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Adds a track to a sequence.</summary>
/// <param name="Kind">What it carries.</param>
/// <param name="Name">Its display name. Defaults to V1, A2 and so on.</param>
/// <param name="Order">Where it sits in the stack. Defaults to the top.</param>
/// <param name="SequenceId">Which sequence. Defaults to the active one.</param>
/// <param name="TrackId">The identifier to give it. A fresh one when left out.</param>
[Command("track.add", Description = "Add a track")]
public sealed record AddTrackCommand(
    [property: Arg(0, "video, audio, subtitle or adjustment")] TrackKind Kind,
    [property: Option("name", "The track name")] string? Name = null,
    [property: Option("order", "Where it sits in the stack")] int? Order = null,
    [property: Option("sequence", "Which sequence")] string? SequenceId = null,
    [property: Option("id", "The identifier to give it")] string? TrackId = null) : ICommand;
