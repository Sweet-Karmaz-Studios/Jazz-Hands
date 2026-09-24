namespace JazzHands.Core.Commands;

/// <summary>Moves an effect to another place in its chain. Effects run first to last.</summary>
/// <param name="EffectId">Which effect.</param>
/// <param name="Index">Its new place, from 0.</param>
[Command("effect.move", Description = "Move an effect up or down its chain")]
public sealed record MoveEffectCommand(
    [property: Arg(0, "The effect id")] string EffectId,
    [property: Option("index", "Its new place in the chain, from 0")] int Index) : ICommand;
