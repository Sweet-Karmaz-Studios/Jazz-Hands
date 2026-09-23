using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Breaks the sync lock on clips so they can be edited apart.</summary>
/// <param name="ClipIds">The clips to unlink.</param>
[Command("clip.unlink", Description = "Break the sync lock on clips")]
public sealed record UnlinkClipsCommand(
    [property: Arg(0, "Comma-separated clip ids")] EquatableArray<string> ClipIds) : ICommand;
