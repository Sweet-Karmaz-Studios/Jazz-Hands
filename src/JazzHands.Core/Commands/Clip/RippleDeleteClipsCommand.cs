using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Removes clips and closes the time they took on every sync-locked track.</summary>
/// <remarks>The clips linked to them, such as a picture's sound, go too unless <paramref name="Alone"/>.</remarks>
/// <param name="ClipIds">The clips to remove.</param>
/// <param name="Alone">Remove only these clips, not the clips linked to them.</param>
[Command("clip.ripple-delete", Description = "Remove clips and close the gap on every sync-locked track")]
public sealed record RippleDeleteClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds,
    [property: Option("alone", "Only these clips, not the clips linked to them")] bool Alone = false) : ICommand;
