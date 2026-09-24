using JazzHands.Core.Time;

namespace JazzHands.Core.Commands;

/// <summary>Chooses how a title comes in and goes out.</summary>
/// <remarks>
/// An animation becomes keyframes on the title's animation channels (<c>fade</c>, <c>offset</c>,
/// <c>zoom</c>, <c>blur</c>, <c>reveal</c>), replacing whatever was there, so the result can be
/// edited like any keyframes. The in animation runs from the clip's start, the out one up to its
/// end; what is left out stays as it was.
/// </remarks>
/// <param name="ClipId">The title clip.</param>
/// <param name="In">The animation in: none, fade, slide-left, slide-right, slide-up, slide-down, scale, typewriter, word-reveal, blur or wipe.</param>
/// <param name="InDuration">How long it takes.</param>
/// <param name="Out">The animation out, from the same list.</param>
/// <param name="OutDuration">How long it takes.</param>
[Command("title.set-animation", Description = "Choose how a title comes in and goes out")]
public sealed record SetTitleAnimationCommand(
    [property: Arg(0, "The title clip id")] string ClipId,
    [property: Option("in", "none, fade, slide-left, slide-right, slide-up, slide-down, scale, typewriter, word-reveal, blur or wipe")] string? In = null,
    [property: Option("in-dur", "How long the animation in takes")] Flicks? InDuration = null,
    [property: Option("out", "The animation out, from the same list")] string? Out = null,
    [property: Option("out-dur", "How long the animation out takes")] Flicks? OutDuration = null) : ICommand;
