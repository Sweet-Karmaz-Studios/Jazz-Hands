using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Moves a clip along the timeline, taking the time out of its neighbours.</summary>
/// <remarks>The clip keeps what it shows; the clips either side grow and shrink to make room.</remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="By">How far to move it. Negative moves it earlier.</param>
[Command("clip.slide", Description = "Move a clip, taking the time out of its neighbours")]
public sealed record SlideClipCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("by", "How far to move it")] Flicks By) : ICommand;
