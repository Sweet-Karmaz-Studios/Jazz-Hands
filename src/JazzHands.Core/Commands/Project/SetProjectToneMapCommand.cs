using JazzHands.Core.Model;

namespace JazzHands.Core.Commands;

/// <summary>Sets how HDR clips are tone mapped when they do not say otherwise.</summary>
/// <remarks>
/// Only what is given changes. The source's peak is a property of each file, so it is set per clip
/// with <c>clip.set-tone-map --peak</c>, not here.
/// </remarks>
/// <param name="Operator">bt2390, hable, mobius or clip.</param>
/// <param name="Desaturate">How much compressed highlights lose their colour, 0 to 1.</param>
[Command("project.set-tone-map", Description = "Set how HDR clips are brought down to SDR by default")]
public sealed record SetProjectToneMapCommand(
    [property: Option("operator", "bt2390, hable, mobius or clip")] ToneMapOperator? Operator = null,
    [property: Option("desaturate", "How much compressed highlights lose their colour, 0 to 1")] double? Desaturate = null) : ICommand;
