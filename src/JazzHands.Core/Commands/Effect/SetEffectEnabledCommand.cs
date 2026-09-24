namespace JazzHands.Core.Commands;

/// <summary>Turns an effect off without removing it, or back on.</summary>
/// <param name="EffectId">Which effect.</param>
/// <param name="Enabled">True to run it, false to bypass it.</param>
[Command("effect.set-enabled", Description = "Bypass an effect or turn it back on")]
public sealed record SetEffectEnabledCommand(
    [property: Arg(0, "The effect id")] string EffectId,
    [property: Arg(1, "true to run it, false to bypass it")] bool Enabled) : ICommand;
