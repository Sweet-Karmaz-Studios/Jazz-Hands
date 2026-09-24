using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Removes clips and closes the time they took on every sync-locked track.</summary>
/// <param name="ClipIds">The clips to remove.</param>
[Command("clip.ripple-delete", Description = "Remove clips and close the gap on every sync-locked track")]
public sealed record RippleDeleteClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds) : ICommand;
