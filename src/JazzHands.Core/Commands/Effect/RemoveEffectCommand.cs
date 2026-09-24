namespace JazzHands.Core.Commands;

/// <summary>Takes an effect off whatever it is on.</summary>
/// <param name="EffectId">Which effect.</param>
[Command("effect.remove", Description = "Remove an effect")]
public sealed record RemoveEffectCommand(
    [property: Arg(0, "The effect id")] string EffectId) : ICommand;
