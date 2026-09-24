namespace JazzHands.Core.Commands;

/// <summary>Adds an effect to a clip or a track.</summary>
/// <remarks>
/// A picture effect goes on a clip on a video or adjustment track, or on one of those tracks; a
/// sound effect on an audio clip or an audio track. Every parameter starts at its default and
/// nothing is stored until one is changed. <c>jazz effect list</c> shows the types.
/// </remarks>
/// <param name="OwnerId">The clip or track.</param>
/// <param name="TypeId">Which effect, for example video.blur.gaussian.</param>
/// <param name="Index">Where in the chain, from 0; the end when not given.</param>
/// <param name="EffectId">The identifier for the new effect.</param>
[Command("effect.add", Description = "Add an effect to a clip or a track")]
public sealed record AddEffectCommand(
    [property: Arg(0, "The clip or track id")] string OwnerId,
    [property: Arg(1, "The effect type, for example video.blur.gaussian")] string TypeId,
    [property: Option("index", "Where in the chain, from 0; the end when not given")] int? Index = null,
    [property: Option("id", "The identifier for the new effect")] string? EffectId = null) : ICommand;
