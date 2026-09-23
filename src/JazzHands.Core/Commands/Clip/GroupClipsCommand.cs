using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Groups clips so that selecting one selects them all.</summary>
/// <remarks>
/// A group is a selection convenience and nothing more: grouped clips can still be trimmed and
/// moved independently once selected. Linking, which keeps clips in sync, is a different thing;
/// see <c>clip.link</c>.
/// </remarks>
/// <param name="ClipIds">The clips to group.</param>
/// <param name="GroupId">The identifier to share. A fresh one when left out.</param>
[Command("clip.group", Description = "Group clips so that selecting one selects them all")]
public sealed record GroupClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds,
    [property: Option("id", "The identifier to share")] string? GroupId = null) : ICommand;
