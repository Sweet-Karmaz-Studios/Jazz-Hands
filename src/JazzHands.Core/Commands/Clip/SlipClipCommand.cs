using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Changes which part of the source a clip shows, without moving the clip.</summary>
/// <param name="ClipId">Which clip.</param>
/// <param name="By">How far through the source to slide. Negative goes earlier.</param>
[Command("clip.slip", Description = "Change which part of the source a clip shows")]
public sealed record SlipClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("by", "How far through the source to slide")] Flicks By) : ICommand;
