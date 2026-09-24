using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Holds the frame of a clip at a time, pushing the rest of the clip and everything after it on.</summary>
/// <remarks>
/// The clip is cut at the frame and a freeze frame of it goes into the space that opens, on every
/// sync-locked track. The hold plays no sound, and trimming it never changes the frame it holds.
/// </remarks>
/// <param name="ClipId">Which clip.</param>
/// <param name="At">The frame to hold, on the timeline.</param>
/// <param name="Duration">How long to hold it. Two seconds when left out.</param>
/// <param name="NewClipId">The identifier for the freeze frame. A fresh one when left out.</param>
[Command("clip.freeze-frame", Description = "Hold a frame for a while, pushing the rest on")]
public sealed record FreezeFrameCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("at", "The frame to hold")] Flicks At,
    [property: Option("dur", "How long to hold it; two seconds when left out")] Flicks? Duration = null,
    [property: Option("id", "The identifier for the freeze frame")] string? NewClipId = null) : ICommand;
