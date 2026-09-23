namespace JazzHands.Core.Commands;

/// <summary>Takes a clip off its track.</summary>
/// <param name="ClipId">Which clip.</param>
/// <param name="Ripple">Close the gap by pulling everything after it back.</param>
[Command("clip.remove", Description = "Remove a clip, leaving a gap or closing it")]
public sealed record RemoveClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("ripple", "Close the gap behind it")] bool Ripple = false) : ICommand;
