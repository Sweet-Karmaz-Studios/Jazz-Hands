namespace JazzHands.Core.Commands;

/// <summary>Puts an effect's parameters back to their defaults, keyframes and all.</summary>
/// <param name="EffectId">Which effect.</param>
/// <param name="Param">Only this parameter; all of them when not given.</param>
[Command("effect.reset", Description = "Put an effect's parameters back to their defaults")]
public sealed record ResetEffectCommand(
    [property: Arg(0, "The effect id")] string EffectId,
    [property: Option("param", "Only this parameter; all of them when not given")] string? Param = null) : ICommand;
