namespace JazzHands.Core.Commands;

/// <summary>Takes out every media item no clip uses, in any sequence.</summary>
/// <param name="KeepTagged">Keep items with tags, which someone sorted on purpose.</param>
[Command("media.remove-unused", Description = "Remove every media item no clip uses")]
public sealed record RemoveUnusedMediaCommand(
    [property: Option("keep-tagged", "Keep items that have tags")] bool KeepTagged = false) : ICommand;
