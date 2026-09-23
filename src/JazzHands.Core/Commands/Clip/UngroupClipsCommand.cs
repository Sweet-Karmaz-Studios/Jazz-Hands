using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Takes clips out of their selection group.</summary>
/// <param name="ClipIds">The clips to ungroup.</param>
[Command("clip.ungroup", Description = "Take clips out of their selection group")]
public sealed record UngroupClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds) : ICommand;
