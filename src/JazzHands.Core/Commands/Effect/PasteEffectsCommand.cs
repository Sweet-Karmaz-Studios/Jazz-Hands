namespace JazzHands.Core.Commands;

/// <summary>Pastes effects that <c>effect.copy</c> returned onto a clip or a track, with new ids.</summary>
/// <param name="OwnerId">The clip or track.</param>
/// <param name="Data">What effect.copy returned.</param>
/// <param name="Index">Where in the chain, from 0; the end when not given.</param>
[Command("effect.paste", Description = "Paste copied effects onto a clip or a track")]
public sealed record PasteEffectsCommand(
    [property: Arg(0, "The clip or track id")] string OwnerId,
    [property: Option("data", "What effect.copy returned")] string Data,
    [property: Option("index", "Where in the chain, from 0; the end when not given")] int? Index = null) : ICommand;
