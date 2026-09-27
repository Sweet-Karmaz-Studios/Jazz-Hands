using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Sets how a clip's speed curve leaves a point: straight, eased in or out, or held (Phase 45).</summary>
/// <param name="ClipId">The clip.</param>
/// <param name="Point">Which point, from 1 in time order.</param>
/// <param name="Ease">linear, easeIn, easeOut, easeInOut or hold.</param>
[Command("clip.remap-set-ease", Description = "Set how a clip's speed curve leaves a point")]
public sealed record RemapSetEaseCommand(
    [property: Arg(0, "The clip id")] string ClipId,
    [property: Option("point", "Which point, from 1 in time order")] int Point,
    [property: Option("ease", "linear, easeIn, easeOut, easeInOut or hold")] Interp Ease) : ICommand;
