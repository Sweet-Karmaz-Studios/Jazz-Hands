using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Links clips so that moving one moves them all.</summary>
/// <remarks>
/// This is the A and V sync lock: a camera clip and its sound are linked on insert, and every
/// move, trim and ripple after that applies to both. Grouping, which only makes clips select
/// together, is a different thing; see <c>clip.group</c>.
/// </remarks>
/// <param name="ClipIds">The clips to link.</param>
/// <param name="LinkGroupId">The identifier to share. A fresh one when left out.</param>
[Command("clip.link", Description = "Link clips so that moving one moves them all")]
public sealed record LinkClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds,
    [property: Option("id", "The identifier to share")] string? LinkGroupId = null) : ICommand;
