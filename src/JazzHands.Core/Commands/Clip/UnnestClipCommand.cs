namespace JazzHands.Core.Commands;

/// <summary>Replaces a compound clip with the clips inside it.</summary>
/// <remarks>
/// The nested sequence keeps its own tracks, so its clips are laid back down on the tracks that
/// match by kind and order, offset to where the compound clip sat. The nested sequence is removed
/// when nothing else uses it.
/// </remarks>
/// <param name="ClipId">The compound clip.</param>
[Command("clip.unnest", Description = "Replace a compound clip with the clips inside it")]
public sealed record UnnestClipCommand(
    [property: Arg(0, "The compound clip id")] string ClipId) : ICommand;
