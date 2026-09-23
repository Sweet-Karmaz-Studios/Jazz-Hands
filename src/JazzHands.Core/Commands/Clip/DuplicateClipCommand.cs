namespace JazzHands.Core.Commands;

/// <summary>Puts a copy of a clip immediately after it on the same track.</summary>
/// <param name="ClipId">Which clip.</param>
/// <param name="NewClipId">The identifier for the copy. A fresh one when left out.</param>
[Command("clip.duplicate", Description = "Copy a clip to immediately after itself")]
public sealed record DuplicateClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("id", "The identifier for the copy")] string? NewClipId = null) : ICommand;
