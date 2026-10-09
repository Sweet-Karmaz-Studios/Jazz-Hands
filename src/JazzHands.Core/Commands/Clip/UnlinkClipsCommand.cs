using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Unlinks clips so each moves on its own, as <c>clip.link</c> joined them.</summary>
/// <param name="ClipIds">The clips to unlink.</param>
[Command("clip.unlink", Description = "Unlink clips so each moves on its own")]
public sealed record UnlinkClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds) : ICommand;
