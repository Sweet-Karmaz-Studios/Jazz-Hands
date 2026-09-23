using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Moves clips into a new sequence and leaves a compound clip in their place.</summary>
/// <param name="ClipIds">The clips to nest.</param>
/// <param name="Name">The name of the new sequence.</param>
/// <param name="NewClipId">The identifier for the compound clip. A fresh one when left out.</param>
[Command("clip.nest", Description = "Move clips into a new sequence and leave a compound clip behind")]
public sealed record NestClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds,
    [property: Arg(1, "The name of the new sequence")] string Name,
    [property: Option("id", "The identifier for the compound clip")] string? NewClipId = null) : ICommand;
