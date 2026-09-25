using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Sets how a clip shows the moments between its source frames when it plays at another speed.</summary>
/// <remarks>
/// Slow motion and time remapping land between source frames. <c>nearest</c> shows the frame
/// that started before the moment, which steps; <c>blend</c> crossfades the frames either side by
/// how far between them it is, which is smoother. <c>optical-flow</c> is a placeholder that
/// blends in this version.
/// </remarks>
/// <param name="ClipId">Which clip. It must be a clip of a media file.</param>
/// <param name="Mode">nearest, blend or optical-flow.</param>
[Command("clip.set-retime", Description = "Set how a clip shows moments between source frames: nearest frame or a blend")]
public sealed record SetClipRetimeCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Arg(1, "nearest, blend or optical-flow (which blends in this version)")] RetimeMode Mode) : ICommand;
